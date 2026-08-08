namespace YC.EntityFrameworkCore.ScyllaDB;

/// <summary>
///     The order rows are stored in on disk within a partition.
/// </summary>
/// <remarks>
///     This is not a query-time preference — it is physical layout. CQL can only return rows in this order or
///     its exact reverse, so choosing it decides which queries are cheap forever after.
/// </remarks>
public enum ScyllaClusteringOrder
{
    Ascending,
    Descending
}

/// <summary>
///     Which CQL collection type a property maps to.
/// </summary>
/// <remarks>
///     Needed because <c>list&lt;T&gt;</c> and <c>set&lt;T&gt;</c> both surface as <c>IEnumerable&lt;T&gt;</c>
///     in .NET, so the CLR type alone cannot say which was meant.
/// </remarks>
public enum ScyllaCollectionKind
{
    List,
    Set,
    Map
}
