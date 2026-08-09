using YC.EntityFrameworkCore.ScyllaDB;
using YC.EntityFrameworkCore.ScyllaDB.Query.Internal;

// ReSharper disable once CheckNamespace
namespace Microsoft.EntityFrameworkCore;

/// <summary>
///     Query helpers specific to ScyllaDB.
/// </summary>
public static class ScyllaQueryableExtensions
{
    internal static readonly System.Reflection.MethodInfo PerPartitionLimitMethod =
        typeof(ScyllaQueryableExtensions).GetMethod(nameof(PerPartitionLimit))!;

    /// <summary>
    ///     Caps the rows read from each partition, rather than from the result as a whole.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The one thing <c>Take</c> cannot express. "The newest three readings
    ///         from every sensor" is a single query with this and one query per sensor without it, because a
    ///         plain <c>LIMIT</c> would stop after three rows total.
    ///     </para>
    ///     <para>
    ///         It only earns its keep on a query that reads more than one partition — through <c>Contains</c>
    ///         over several keys, a secondary index, or an explicit scan. Within a single partition it does the
    ///         same job as <c>Take</c>.
    ///     </para>
    /// </remarks>
    public static IQueryable<TEntity> PerPartitionLimit<TEntity>(this IQueryable<TEntity> source, int limit)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        return source.Provider.CreateQuery<TEntity>(
            System.Linq.Expressions.Expression.Call(
                PerPartitionLimitMethod.MakeGenericMethod(typeof(TEntity)),
                source.Expression,
                System.Linq.Expressions.Expression.Constant(limit)));
    }

    /// <summary>
    ///     Returns the CQL this query would run, without running it.
    /// </summary>
    /// <remarks>
    ///     Useful for checking which restrictions actually reached the server, since CQL's rules make the
    ///     difference between a partition seek and a cluster-wide scan.
    /// </remarks>
    public static string ToCqlString<TEntity>(this IQueryable<TEntity> source)
    {
        ArgumentNullException.ThrowIfNull(source);

        return Compile(source).ToCqlString();
    }

    /// <summary>
    ///     Reads a single page of results and returns the cursor for the next one.
    /// </summary>
    /// <param name="source">The query to page through.</param>
    /// <param name="pageSize">How many rows to read.</param>
    /// <param name="continuationToken">
    ///     The cursor from the previous page, or null to start at the beginning.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <remarks>
    ///     <para>
    ///         This is how ScyllaDB pages, and why <c>Skip</c> cannot be translated: CQL has no OFFSET, because
    ///         rows are reached by seeking rather than by counting past the ones before them. A cursor records
    ///         where the last page stopped.
    ///     </para>
    ///     <para>
    ///         <strong>The token is not signed or encrypted.</strong> A tampered one can be used to read other
    ///         partitions, so sign it before handing it to an untrusted client, and verify it on the way back.
    ///     </para>
    /// </remarks>
    public static Task<ScyllaPage<TEntity>> ToPageAsync<TEntity>(
        this IQueryable<TEntity> source,
        int pageSize,
        string? continuationToken = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        return Compile(source).ToPageAsync<TEntity>(pageSize, continuationToken, cancellationToken);
    }

    // Executing the compiled query only constructs the enumerable; nothing is sent until it is enumerated.
    private static IScyllaQueryingEnumerable Compile<TEntity>(IQueryable<TEntity> source)
        => source.Provider.Execute<IEnumerable<TEntity>>(source.Expression) as IScyllaQueryingEnumerable
           ?? throw new InvalidOperationException(
               "This operation is only available on a query backed by the ScyllaDB provider.");
}
