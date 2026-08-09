using YC.EntityFrameworkCore.ScyllaDB.Query.Internal;

// ReSharper disable once CheckNamespace
namespace Microsoft.EntityFrameworkCore;

/// <summary>
///     Query helpers specific to ScyllaDB.
/// </summary>
public static class ScyllaQueryableExtensions
{
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

        // Executing the compiled query only constructs the enumerable; nothing is sent until it is enumerated.
        return source.Provider.Execute<IEnumerable<TEntity>>(source.Expression) is IScyllaQueryingEnumerable queryingEnumerable
            ? queryingEnumerable.ToCqlString()
            : throw new InvalidOperationException(
                "'ToCqlString' is only available on a query backed by the ScyllaDB provider.");
    }
}
