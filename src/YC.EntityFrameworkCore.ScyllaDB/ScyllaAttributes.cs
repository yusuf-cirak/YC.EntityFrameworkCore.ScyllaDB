namespace YC.EntityFrameworkCore.ScyllaDB;

/// <summary>
///     Maps an entity type to a CQL table, and optionally to a keyspace other than the context's default.
/// </summary>
/// <remarks>
///     Named with a <c>Scylla</c> prefix to avoid colliding with
///     <see cref="System.ComponentModel.DataAnnotations.Schema.TableAttribute" />, which entity classes commonly
///     already carry.
/// </remarks>
[AttributeUsage(AttributeTargets.Class)]
public sealed class ScyllaTableAttribute : Attribute
{
    public ScyllaTableAttribute(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        Name = name;
    }

    public string Name { get; }

    public string? Keyspace { get; init; }
}

/// <summary>
///     Marks a property as part of the partition key.
/// </summary>
/// <remarks>
///     The partition key decides which node owns a row, so every read must restrict it. Use
///     <see cref="Order" /> when the key spans several properties; the order is part of the table's identity and
///     cannot be changed afterwards without rebuilding the table.
/// </remarks>
[AttributeUsage(AttributeTargets.Property)]
public sealed class PartitionKeyAttribute : Attribute
{
    public PartitionKeyAttribute(int order = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(order);

        Order = order;
    }

    public int Order { get; }
}

/// <summary>
///     Marks a property as a clustering key, which orders rows inside a partition.
/// </summary>
/// <remarks>
///     Clustering columns must be restricted as a contiguous prefix in a query — you cannot skip one — so their
///     order determines which queries are possible at all.
/// </remarks>
[AttributeUsage(AttributeTargets.Property)]
public sealed class ClusteringKeyAttribute : Attribute
{
    public ClusteringKeyAttribute(int order = 0, ScyllaClusteringOrder direction = ScyllaClusteringOrder.Ascending)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(order);

        Order = order;
        Direction = direction;
    }

    public int Order { get; }

    public ScyllaClusteringOrder Direction { get; }
}

/// <summary>
///     Marks a column as static, so it is stored once per partition rather than once per row.
/// </summary>
/// <remarks>
///     Requires the table to have at least one clustering column: without one, every partition holds a single
///     row and "once per partition" would be meaningless.
/// </remarks>
[AttributeUsage(AttributeTargets.Property)]
public sealed class StaticColumnAttribute : Attribute;

/// <summary>
///     Marks a property as a CQL counter.
/// </summary>
/// <remarks>
///     Counters are a separate world: a table is either all counters plus its primary key, or has none. They
///     cannot be inserted or read-modify-written, only incremented, so they do not participate in EF Core's
///     usual snapshot change tracking.
/// </remarks>
[AttributeUsage(AttributeTargets.Property)]
public sealed class CounterAttribute : Attribute;

/// <summary>
///     Sets the CQL type for a column explicitly, overriding whatever the CLR type would map to.
/// </summary>
/// <remarks>
///     The escape hatch for types the provider cannot infer — <c>set&lt;int&gt;</c> versus
///     <c>list&lt;int&gt;</c>, <c>frozen&lt;address&gt;</c>, <c>vector&lt;float, 384&gt;</c>.
/// </remarks>
[AttributeUsage(AttributeTargets.Property)]
public sealed class CqlTypeAttribute : Attribute
{
    public CqlTypeAttribute(string cqlType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cqlType);

        CqlType = cqlType;
    }

    public string CqlType { get; }
}
