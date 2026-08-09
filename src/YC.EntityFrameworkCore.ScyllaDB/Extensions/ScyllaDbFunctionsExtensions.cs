// ReSharper disable once CheckNamespace
namespace Microsoft.EntityFrameworkCore;

/// <summary>
///     ScyllaDB-specific functions usable inside a LINQ query.
/// </summary>
public static class ScyllaDbFunctionsExtensions
{
    private const string OnlyForQuerying =
        "This method is only for use inside an EF Core LINQ query, where the provider translates it to CQL.";

    /// <summary>
    ///     Opts the query into <c>ALLOW FILTERING</c>, permitting restrictions CQL would otherwise refuse.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         This is not a hint — it changes what the query costs. Without it, ScyllaDB only answers queries
    ///         it can serve by seeking directly to the data. With it, the coordinator may scan every row in
    ///         every partition and discard most of them, so the cost grows with the size of the table rather
    ///         than with the size of the result.
    ///     </para>
    ///     <para>
    ///         Reach for it for small tables, administrative queries, and one-off work — not for a request path,
    ///         and never on a filter an untrusted caller controls.
    ///     </para>
    /// </remarks>
    public static bool AllowFiltering(this DbFunctions _)
        => throw new InvalidOperationException(OnlyForQuerying);
}
