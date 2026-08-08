using Microsoft.EntityFrameworkCore.Query;

namespace YC.EntityFrameworkCore.ScyllaDB.Query.Internal;

/// <summary>
///     Per-execution state for a query.
/// </summary>
public class ScyllaQueryContext : QueryContext
{
    public ScyllaQueryContext(QueryContextDependencies dependencies)
        : base(dependencies)
    {
    }
}

/// <inheritdoc cref="IQueryContextFactory" />
public class ScyllaQueryContextFactory : IQueryContextFactory
{
    private readonly QueryContextDependencies _dependencies;

    public ScyllaQueryContextFactory(QueryContextDependencies dependencies)
    {
        ArgumentNullException.ThrowIfNull(dependencies);

        _dependencies = dependencies;
    }

    public virtual QueryContext Create()
        => new ScyllaQueryContext(_dependencies);
}
