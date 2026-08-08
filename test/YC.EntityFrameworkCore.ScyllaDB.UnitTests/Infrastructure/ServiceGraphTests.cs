using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using YC.EntityFrameworkCore.ScyllaDB.Storage.Internal;
using YC.EntityFrameworkCore.ScyllaDB.UnitTests.TestUtilities;

namespace YC.EntityFrameworkCore.ScyllaDB.UnitTests.Infrastructure;

/// <summary>
///     Proves the provider's service graph resolves end to end. A provider that registers an incomplete set of
///     services fails only when a context is first built, which is exactly what these pin down.
/// </summary>
public class ServiceGraphTests
{
    [Fact]
    public void UseScyllaDb_registers_the_provider_as_the_database_provider()
    {
        using var context = ScyllaTestContext.Create();

        var provider = context.GetService<IDatabaseProvider>();

        Assert.Equal(typeof(ScyllaDatabaseWrapper).Assembly.GetName().Name, provider.Name);
    }

    [Theory]
    [InlineData(typeof(IDatabase), typeof(ScyllaDatabaseWrapper))]
    [InlineData(typeof(IDatabaseCreator), typeof(ScyllaDatabaseCreator))]
    [InlineData(typeof(IDbContextTransactionManager), typeof(ScyllaTransactionManager))]
    [InlineData(typeof(IExecutionStrategyFactory), typeof(ScyllaExecutionStrategyFactory))]
    [InlineData(typeof(ITypeMappingSource), typeof(ScyllaTypeMappingSource))]
    [InlineData(typeof(IScyllaClientWrapper), typeof(ScyllaClientWrapper))]
    [InlineData(typeof(IScyllaSingletonClientWrapper), typeof(ScyllaSingletonClientWrapper))]
    public void Provider_services_resolve_to_the_ScyllaDB_implementations(Type serviceType, Type implementationType)
    {
        using var context = ScyllaTestContext.Create();

        var service = ((IInfrastructure<IServiceProvider>)context).Instance.GetService(serviceType);

        Assert.IsType(implementationType, service);
    }

    [Fact]
    public void Execution_strategy_does_not_retry_because_the_driver_owns_retries()
    {
        using var context = ScyllaTestContext.Create();

        var strategy = context.GetService<IExecutionStrategyFactory>().Create();

        Assert.False(strategy.RetriesOnFailure);
    }

    [Fact]
    public void Building_a_context_does_not_open_a_connection()
    {
        // The contact point is never reachable; if constructing the context dialled it, this would throw.
        using var context = ScyllaTestContext.Create();

        Assert.NotNull(context.Model);
    }
}
