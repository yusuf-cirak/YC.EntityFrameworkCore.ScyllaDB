using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using YC.EntityFrameworkCore.ScyllaDB.Storage.Internal;

namespace YC.EntityFrameworkCore.ScyllaDB.UnitTests.TestUtilities;

/// <summary>
///     Builds a model and generates its CQL without touching a database.
/// </summary>
/// <remarks>
///     Model building and DDL generation are pure functions of the configuration, so the whole schema surface
///     can be asserted as strings. The contact point below is never dialled.
/// </remarks>
public static class ScyllaModelFixture
{
    private sealed class ConfigurableContext(DbContextOptions options, Action<ModelBuilder> configure)
        : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => configure(modelBuilder);
    }

    public static DbContext CreateContext(Action<ModelBuilder> configure, string keyspace = "test_keyspace")
    {
        var options = new DbContextOptionsBuilder<ConfigurableContext>()
            .UseScyllaDb(["127.0.0.1"], keyspace)
            // Each test builds its own model, so the model cache must not hand back another test's.
            .ReplaceService<IModelCacheKeyFactory, NeverCachingModelCacheKeyFactory>()
            .Options;

        return new ConfigurableContext(options, configure);
    }

    public static IModel BuildModel(Action<ModelBuilder> configure, string keyspace = "test_keyspace")
    {
        using var context = CreateContext(configure, keyspace);
        return context.Model;
    }

    public static string CreateTableCql<TEntity>(Action<ModelBuilder> configure, string keyspace = "test_keyspace")
        where TEntity : class
    {
        using var context = CreateContext(configure, keyspace);
        var entityType = context.Model.FindEntityType(typeof(TEntity))!;

        return context.GetService<IScyllaDdlGenerator>().CreateTable(entityType);
    }

    public static string CreateKeyspaceCql(Action<ModelBuilder> configure, string keyspace = "test_keyspace")
    {
        using var context = CreateContext(configure, keyspace);

        return context.GetService<IScyllaDdlGenerator>().CreateKeyspace(context.Model, keyspace);
    }

    /// <summary>
    ///     Returns the exception thrown while building the model, or null if it validated.
    /// </summary>
    public static Exception? CaptureModelError(Action<ModelBuilder> configure, string keyspace = "test_keyspace")
    {
        try
        {
            BuildModel(configure, keyspace);
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private sealed class NeverCachingModelCacheKeyFactory : IModelCacheKeyFactory
    {
        public object Create(DbContext context, bool designTime)
            => new object();
    }
}
