using System.Globalization;
using System.Text;

namespace YC.EntityFrameworkCore.ScyllaDB.Scaffolding;

/// <summary>
///     Controls the names and namespace of scaffolded code.
/// </summary>
public sealed class ScyllaScaffoldingOptions
{
    public string Namespace { get; init; } = "GeneratedModel";

    public string ContextName { get; init; } = "ScyllaDbContext";

    /// <summary>
    ///     Whether <c>snake_case</c> database names become <c>PascalCase</c> in code.
    /// </summary>
    /// <remarks>
    ///     On by default because CQL identifiers are conventionally lower snake case, which reads poorly as C#.
    ///     Turn it off to keep the database spelling exactly.
    /// </remarks>
    public bool PascalCaseNames { get; init; } = true;

    public string ToTypeName(string tableName)
        => Singularise(ToPascalCase(tableName));

    public string ToDbSetName(string tableName)
        => ToPascalCase(tableName);

    public string ToPropertyName(string columnName)
        => ToPascalCase(columnName);

    private string ToPascalCase(string name)
    {
        if (!PascalCaseNames)
        {
            return name;
        }

        var builder = new StringBuilder(name.Length);
        var capitalise = true;

        foreach (var character in name)
        {
            if (character is '_' or '-' or ' ')
            {
                capitalise = true;
                continue;
            }

            builder.Append(capitalise ? char.ToUpper(character, CultureInfo.InvariantCulture) : character);
            capitalise = false;
        }

        return builder.Length == 0 ? name : builder.ToString();
    }

    /// <summary>
    ///     Turns a plural table name into a singular type name.
    /// </summary>
    /// <remarks>
    ///     Deliberately crude — it handles the endings that actually show up in table names and leaves anything
    ///     else alone. Scaffolded code is a starting point that gets edited, so a wrong guess costs a rename,
    ///     not a bug.
    /// </remarks>
    private static string Singularise(string name)
        => name switch
        {
            _ when name.EndsWith("ies", StringComparison.Ordinal) && name.Length > 3
                => string.Concat(name.AsSpan(0, name.Length - 3), "y"),
            _ when name.EndsWith("sses", StringComparison.Ordinal)
                => name[..^2],
            _ when name.EndsWith('s') && !name.EndsWith("ss", StringComparison.Ordinal)
                => name[..^1],
            _ => name
        };
}

/// <summary>
///     Maps CQL type names to the CLR types the provider reads them as.
/// </summary>
internal static class ScyllaCqlTypeNames
{
    private static readonly Dictionary<string, string> Scalars = new(StringComparer.Ordinal)
    {
        ["ascii"] = "string?",
        ["text"] = "string?",
        ["varchar"] = "string?",
        ["boolean"] = "bool",
        ["blob"] = "byte[]?",
        ["tinyint"] = "sbyte",
        ["smallint"] = "short",
        ["int"] = "int",
        ["bigint"] = "long",
        ["counter"] = "long",
        ["float"] = "float",
        ["double"] = "double",
        ["decimal"] = "decimal",
        ["uuid"] = "Guid",
        ["timeuuid"] = "Cassandra.TimeUuid",
        ["timestamp"] = "DateTimeOffset",
        ["date"] = "Cassandra.LocalDate?",
        ["time"] = "Cassandra.LocalTime?",
        ["duration"] = "Cassandra.Duration",
        ["varint"] = "System.Numerics.BigInteger",
        ["inet"] = "System.Net.IPAddress?"
    };

    public static string ToClrTypeName(string cqlType)
    {
        if (Scalars.TryGetValue(cqlType, out var scalar))
        {
            return scalar;
        }

        if (TryReadCollection(cqlType, "list<", out var listElement))
        {
            return $"List<{ToClrTypeName(listElement)}>?";
        }

        if (TryReadCollection(cqlType, "set<", out var setElement))
        {
            return $"HashSet<{ToClrTypeName(setElement)}>?";
        }

        if (TryReadCollection(cqlType, "map<", out var mapArguments))
        {
            var separator = mapArguments.IndexOf(',', StringComparison.Ordinal);

            if (separator > 0)
            {
                var key = ToClrTypeName(mapArguments[..separator].Trim());
                var value = ToClrTypeName(mapArguments[(separator + 1)..].Trim());

                return $"Dictionary<{key}, {value}>?";
            }
        }

        if (cqlType.StartsWith("frozen<", StringComparison.Ordinal))
        {
            return ToClrTypeName(cqlType[7..^1]);
        }

        // Anything left — a user-defined type, a tuple, a vector — needs a hand-written mapping, so the
        // generated property says so rather than guessing.
        return "object? /* " + cqlType + " — needs a CLR type */";
    }

    /// <summary>
    ///     Whether the provider would have to be told this type explicitly rather than inferring it.
    /// </summary>
    public static bool NeedsExplicitCqlType(string cqlType)
        => cqlType.StartsWith("list<", StringComparison.Ordinal)
           || cqlType.StartsWith("set<", StringComparison.Ordinal)
           || cqlType.StartsWith("map<", StringComparison.Ordinal)
           || cqlType.StartsWith("frozen<", StringComparison.Ordinal)
           || cqlType is "ascii" or "varchar";

    private static bool TryReadCollection(string cqlType, string prefix, out string inner)
    {
        if (cqlType.StartsWith(prefix, StringComparison.Ordinal) && cqlType.EndsWith('>'))
        {
            inner = cqlType[prefix.Length..^1];
            return true;
        }

        inner = string.Empty;
        return false;
    }
}
