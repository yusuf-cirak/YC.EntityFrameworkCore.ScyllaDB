using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Storage.Json;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace YC.EntityFrameworkCore.ScyllaDB.Storage.Internal;

/// <summary>
///     A CLR type paired with the CQL type it is stored as.
/// </summary>
public class ScyllaTypeMapping : CoreTypeMapping
{
    public ScyllaTypeMapping(Type clrType, string cqlTypeName)
        : this(new CoreTypeMappingParameters(clrType), cqlTypeName)
    {
    }

    protected ScyllaTypeMapping(CoreTypeMappingParameters parameters, string cqlTypeName)
        : base(parameters)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cqlTypeName);

        CqlTypeName = cqlTypeName;
    }

    /// <summary>
    ///     The CQL type name as it appears in DDL, for example <c>text</c>, <c>bigint</c> or <c>set&lt;int&gt;</c>.
    /// </summary>
    public virtual string CqlTypeName { get; }

    protected override CoreTypeMapping Clone(CoreTypeMappingParameters parameters)
        => new ScyllaTypeMapping(parameters, CqlTypeName);

    public override CoreTypeMapping WithComposedConverter(
        ValueConverter? converter,
        ValueComparer? comparer = null,
        ValueComparer? keyComparer = null,
        CoreTypeMapping? elementMapping = null,
        JsonValueReaderWriter? jsonValueReaderWriter = null)
        => new ScyllaTypeMapping(
            Parameters.WithComposedConverter(converter, comparer, keyComparer, elementMapping, jsonValueReaderWriter),
            CqlTypeName);
}
