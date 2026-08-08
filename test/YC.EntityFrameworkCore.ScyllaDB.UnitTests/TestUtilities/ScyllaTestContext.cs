using Microsoft.EntityFrameworkCore;

namespace YC.EntityFrameworkCore.ScyllaDB.UnitTests.TestUtilities;

/// <summary>
///     A context with no entity types, configured against contact points that are never dialled.
/// </summary>
/// <remarks>
///     Building a context does not open a connection — the driver session is created lazily on first use — so
///     everything here runs without a database. An empty model also avoids needing type mappings, which do not
///     exist yet.
/// </remarks>
public class ScyllaTestContext : DbContext
{
    public ScyllaTestContext(DbContextOptions options)
        : base(options)
    {
    }

    public static ScyllaTestContext Create(Action<DbContextOptionsBuilder>? configure = null)
    {
        var builder = new DbContextOptionsBuilder<ScyllaTestContext>()
            .UseScyllaDb(["127.0.0.1"], "unit_tests_only");

        configure?.Invoke(builder);

        return new ScyllaTestContext(builder.Options);
    }
}
