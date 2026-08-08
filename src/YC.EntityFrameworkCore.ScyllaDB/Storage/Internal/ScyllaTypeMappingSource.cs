using System.Collections;
using System.Globalization;
using System.Net;
using System.Numerics;
using Cassandra;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;

namespace YC.EntityFrameworkCore.ScyllaDB.Storage.Internal;

/// <summary>
///     Maps CLR types onto CQL types.
/// </summary>
public class ScyllaTypeMappingSource : TypeMappingSource
{
    /// <summary>
    ///     The scalar CLR-to-CQL table.
    /// </summary>
    /// <remarks>
    ///     <c>date</c>, <c>time</c> and <c>duration</c> map to the driver's own <see cref="LocalDate" />,
    ///     <see cref="LocalTime" /> and <see cref="Duration" /> rather than to <see cref="DateOnly" />,
    ///     <see cref="TimeOnly" /> and <see cref="TimeSpan" />. Converters for the framework types come with the
    ///     value-conversion work; <c>Duration</c> in particular is wider than <c>TimeSpan</c>, since it carries
    ///     months separately, so that conversion is not simply lossless in both directions.
    /// </remarks>
    private static readonly Dictionary<Type, string> ClrToCql = new()
    {
        [typeof(string)] = "text",
        [typeof(bool)] = "boolean",
        [typeof(byte[])] = "blob",
        [typeof(sbyte)] = "tinyint",
        [typeof(short)] = "smallint",
        [typeof(int)] = "int",
        [typeof(long)] = "bigint",
        [typeof(float)] = "float",
        [typeof(double)] = "double",
        [typeof(decimal)] = "decimal",
        [typeof(Guid)] = "uuid",
        [typeof(TimeUuid)] = "timeuuid",
        [typeof(DateTimeOffset)] = "timestamp",
        [typeof(DateTime)] = "timestamp",
        [typeof(LocalDate)] = "date",
        [typeof(LocalTime)] = "time",
        [typeof(Duration)] = "duration",
        [typeof(BigInteger)] = "varint",
        [typeof(IPAddress)] = "inet"
    };

    public ScyllaTypeMappingSource(TypeMappingSourceDependencies dependencies)
        : base(dependencies)
    {
    }

    /// <summary>
    ///     Resolves the mapping for a property, honouring an explicit CQL type or collection kind first.
    /// </summary>
    /// <remarks>
    ///     This overload is where collection disambiguation has to happen: <c>list&lt;T&gt;</c> and
    ///     <c>set&lt;T&gt;</c> are indistinguishable from the CLR type alone, and only the property carries the
    ///     annotation that settles it.
    /// </remarks>
    public override CoreTypeMapping? FindMapping(IProperty property)
    {
        ArgumentNullException.ThrowIfNull(property);

        var clrType = property.ClrType;

        if (property.GetCqlType() is { } explicitCqlType)
        {
            return new ScyllaTypeMapping(clrType, explicitCqlType);
        }

        if (property.IsCounter())
        {
            return new ScyllaTypeMapping(clrType, "counter");
        }

        if (TryGetCollectionCqlType(clrType, property.GetCollectionKind(), out var collectionCqlType))
        {
            return new ScyllaTypeMapping(clrType, collectionCqlType);
        }

        return base.FindMapping(property);
    }

    protected override CoreTypeMapping? FindMapping(in TypeMappingInfo mappingInfo)
    {
        // The core TypeMappingInfo carries no store type name — that is a relational notion — so an explicitly
        // configured CQL type is applied in the IProperty overload above, before resolution reaches here.
        if (mappingInfo.ClrType is not { } clrType)
        {
            return null;
        }

        var underlyingType = Nullable.GetUnderlyingType(clrType) ?? clrType;

        if (ClrToCql.TryGetValue(underlyingType, out var cqlType))
        {
            return new ScyllaTypeMapping(clrType, cqlType);
        }

        // An enum stores as its underlying integral type; EF Core supplies the conversion.
        if (underlyingType.IsEnum
            && ClrToCql.TryGetValue(underlyingType.GetEnumUnderlyingType(), out var enumCqlType))
        {
            return new ScyllaTypeMapping(clrType, enumCqlType);
        }

        return TryGetCollectionCqlType(clrType, collectionKind: null, out var inferredCollectionType)
            ? new ScyllaTypeMapping(clrType, inferredCollectionType)
            : null;
    }

    /// <summary>
    ///     Works out the CQL collection type for a CLR collection, using the configured kind when present and
    ///     the CLR shape otherwise.
    /// </summary>
    /// <remarks>
    ///     A bare <see cref="IEnumerable{T}" /> is deliberately left unresolved: it could be either a list or a
    ///     set, and guessing would silently pick a storage shape with different semantics. The model validator
    ///     turns that into an actionable error.
    /// </remarks>
    protected virtual bool TryGetCollectionCqlType(
        Type clrType,
        ScyllaCollectionKind? collectionKind,
        out string cqlType)
    {
        cqlType = string.Empty;

        if (clrType == typeof(string) || clrType == typeof(byte[]) || !typeof(IEnumerable).IsAssignableFrom(clrType))
        {
            return false;
        }

        if (TryGetGenericArguments(clrType, typeof(IDictionary<,>), out var mapArguments))
        {
            if (collectionKind is not null and not ScyllaCollectionKind.Map
                || !TryGetElementCqlType(mapArguments[0], out var keyType)
                || !TryGetElementCqlType(mapArguments[1], out var valueType))
            {
                return false;
            }

            cqlType = $"map<{keyType}, {valueType}>";
            return true;
        }

        if (!TryGetGenericArguments(clrType, typeof(IEnumerable<>), out var elementArguments)
            || !TryGetElementCqlType(elementArguments[0], out var elementType))
        {
            return false;
        }

        var kind = collectionKind ?? InferCollectionKind(clrType);

        if (kind is null)
        {
            return false;
        }

        cqlType = kind == ScyllaCollectionKind.Set ? $"set<{elementType}>" : $"list<{elementType}>";
        return true;
    }

    private static ScyllaCollectionKind? InferCollectionKind(Type clrType)
    {
        if (clrType.IsArray)
        {
            return ScyllaCollectionKind.List;
        }

        if (!clrType.IsGenericType)
        {
            return null;
        }

        var definition = clrType.GetGenericTypeDefinition();

        if (definition == typeof(HashSet<>) || definition == typeof(SortedSet<>) || definition == typeof(ISet<>))
        {
            return ScyllaCollectionKind.Set;
        }

        return definition == typeof(List<>) || definition == typeof(IList<>) || definition == typeof(IReadOnlyList<>)
            ? ScyllaCollectionKind.List
            : null;
    }

    private static bool TryGetElementCqlType(Type elementClrType, out string cqlType)
    {
        var underlying = Nullable.GetUnderlyingType(elementClrType) ?? elementClrType;

        if (ClrToCql.TryGetValue(underlying, out var found))
        {
            cqlType = found;
            return true;
        }

        if (underlying.IsEnum && ClrToCql.TryGetValue(underlying.GetEnumUnderlyingType(), out var enumType))
        {
            cqlType = enumType;
            return true;
        }

        cqlType = string.Empty;
        return false;
    }

    private static bool TryGetGenericArguments(Type clrType, Type openInterface, out Type[] arguments)
    {
        var match = (clrType.IsGenericType && clrType.GetGenericTypeDefinition() == openInterface
                        ? clrType
                        : null)
                    ?? clrType.GetInterfaces()
                        .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == openInterface);

        if (match is null && clrType.IsArray && openInterface == typeof(IEnumerable<>))
        {
            arguments = [clrType.GetElementType()!];
            return true;
        }

        arguments = match?.GetGenericArguments() ?? [];
        return match is not null;
    }

    /// <summary>
    ///     Renders a CQL literal, used by the DDL generator for table options.
    /// </summary>
    public static string FormatLiteral(object value)
        => value switch
        {
            string text => $"'{text.Replace("'", "''", StringComparison.Ordinal)}'",
            bool flag => flag ? "true" : "false",
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty
        };
}
