namespace YC.EntityFrameworkCore.ScyllaDB;

/// <summary>
///     A secondary index on one column.
/// </summary>
/// <param name="PropertyName">The property whose column is indexed.</param>
/// <param name="Name">The index name, or null to let ScyllaDB derive one.</param>
/// <remarks>
///     A secondary index makes a non-key column seekable, which is what turns a full scan back into a lookup.
///     It is not free: ScyllaDB maintains a hidden table behind it, so an index on a high-cardinality column —
///     or on one that changes constantly — costs more than the queries it saves.
/// </remarks>
public sealed record ScyllaIndex(string PropertyName, string? Name = null);

/// <summary>
///     Marks a property as having a secondary index.
/// </summary>
/// <remarks>
///     Restricting an indexed column no longer needs <c>EF.Functions.AllowFiltering()</c>. ScyllaDB will still
///     only use one indexed column per query.
/// </remarks>
[AttributeUsage(AttributeTargets.Property)]
public sealed class SecondaryIndexAttribute : Attribute
{
    public string? Name { get; init; }
}

/// <summary>
///     A page of results, with the cursor needed to fetch the next one.
/// </summary>
/// <param name="Values">The rows in this page.</param>
/// <param name="ContinuationToken">
///     Opaque cursor for the next page, or null when there are no more rows.
/// </param>
/// <remarks>
///     <strong>The token is not signed or encrypted.</strong> It wraps ScyllaDB's raw paging state, and a
///     tampered token can be used to read rows from other partitions. Never hand one to an untrusted client
///     without signing it, and never accept one back unverified.
/// </remarks>
public sealed record ScyllaPage<T>(IReadOnlyList<T> Values, string? ContinuationToken);
