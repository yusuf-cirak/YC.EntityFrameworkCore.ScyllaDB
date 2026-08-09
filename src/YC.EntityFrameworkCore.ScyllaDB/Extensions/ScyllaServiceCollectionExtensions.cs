using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata.Conventions.Infrastructure;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Storage;
using YC.EntityFrameworkCore.ScyllaDB.Diagnostics;
using YC.EntityFrameworkCore.ScyllaDB.Infrastructure.Internal;
using YC.EntityFrameworkCore.ScyllaDB.Metadata.Conventions;
using YC.EntityFrameworkCore.ScyllaDB.Query.Internal;
using YC.EntityFrameworkCore.ScyllaDB.Storage.Internal;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
///     Registers the ScyllaDB provider's services.
/// </summary>
public static class ScyllaServiceCollectionExtensions
{
    /// <summary>
    ///     Adds the services the ScyllaDB provider needs.
    /// </summary>
    /// <remarks>
    ///     Called automatically by <c>UseScyllaDb</c>. Call it directly only when building an EF Core internal
    ///     service provider by hand.
    /// </remarks>
    public static IServiceCollection AddEntityFrameworkScyllaDb(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        new EntityFrameworkServicesBuilder(services)
            .TryAdd<LoggingDefinitions, ScyllaLoggingDefinitions>()
            .TryAdd<IDatabaseProvider, DatabaseProvider<ScyllaOptionsExtension>>()
            .TryAdd<IDatabase, ScyllaDatabaseWrapper>()
            .TryAdd<IDbContextTransactionManager, ScyllaTransactionManager>()
            .TryAdd<IExecutionStrategyFactory, ScyllaExecutionStrategyFactory>()
            .TryAdd<IDatabaseCreator, ScyllaDatabaseCreator>()
            .TryAdd<ITypeMappingSource, ScyllaTypeMappingSource>()
            .TryAdd<IModelValidator, ScyllaModelValidator>()
            .TryAdd<IProviderConventionSetBuilder, ScyllaConventionSetBuilder>()
            .TryAdd<IQueryContextFactory, ScyllaQueryContextFactory>()
            .TryAdd<IEvaluatableExpressionFilterPlugin, ScyllaEvaluatableExpressionFilterPlugin>()
            .TryAdd<IQueryableMethodTranslatingExpressionVisitorFactory,
                ScyllaQueryableMethodTranslatingExpressionVisitorFactory>()
            .TryAdd<IShapedQueryCompilingExpressionVisitorFactory, ScyllaShapedQueryCompilingExpressionVisitorFactory>()
            .TryAdd<ISingletonOptions, IScyllaSingletonOptions>(p => p.GetRequiredService<IScyllaSingletonOptions>())
            .TryAddProviderSpecificServices(
                b => b
                    .TryAddSingleton<IScyllaSingletonOptions, ScyllaSingletonOptions>()
                    .TryAddSingleton<IScyllaSingletonClientWrapper, ScyllaSingletonClientWrapper>()
                    .TryAddScoped<IScyllaClientWrapper, ScyllaClientWrapper>()
                    .TryAddSingleton<IScyllaDdlGenerator, ScyllaDdlGenerator>())
            .TryAddCoreServices();

        return services;
    }
}
