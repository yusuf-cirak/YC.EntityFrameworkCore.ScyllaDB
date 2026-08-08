using Microsoft.EntityFrameworkCore.Storage;

namespace YC.EntityFrameworkCore.ScyllaDB.Storage.Internal;

/// <summary>
///     Supplies a non-retrying execution strategy.
/// </summary>
/// <remarks>
///     The driver already owns retries, speculative execution and host failover through its own policies.
///     Layering an EF Core retry loop on top of that would re-send statements the driver had itself already
///     re-sent — and because CQL writes are blind upserts with no affected-row count, a duplicated
///     non-idempotent write cannot be detected afterwards. Retry configuration belongs on the cluster builder.
/// </remarks>
public class ScyllaExecutionStrategyFactory : IExecutionStrategyFactory
{
    private readonly ExecutionStrategyDependencies _dependencies;

    public ScyllaExecutionStrategyFactory(ExecutionStrategyDependencies dependencies)
    {
        ArgumentNullException.ThrowIfNull(dependencies);

        _dependencies = dependencies;
    }

    public virtual IExecutionStrategy Create()
        => new NonRetryingExecutionStrategy(_dependencies);
}
