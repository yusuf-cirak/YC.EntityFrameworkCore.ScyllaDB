using Cassandra;
using Microsoft.EntityFrameworkCore.Infrastructure;
using YC.EntityFrameworkCore.ScyllaDB.Infrastructure;
using YC.EntityFrameworkCore.ScyllaDB.Infrastructure.Internal;
using YC.EntityFrameworkCore.ScyllaDB.Internal;

// ReSharper disable once CheckNamespace
namespace Microsoft.EntityFrameworkCore;

/// <summary>
///     Configures a <see cref="DbContext" /> to use ScyllaDB.
/// </summary>
public static class ScyllaDbContextOptionsExtensions
{
    /// <summary>
    ///     Connects to a ScyllaDB cluster at the given contact points.
    /// </summary>
    /// <param name="optionsBuilder">The options builder being configured.</param>
    /// <param name="contactPoints">
    ///     One or more node addresses. The driver discovers the rest of the cluster from these, so listing two or
    ///     three is enough.
    /// </param>
    /// <param name="keyspace">The keyspace this context maps to.</param>
    /// <param name="scyllaOptionsAction">Optional further configuration.</param>
    public static DbContextOptionsBuilder UseScyllaDb(
        this DbContextOptionsBuilder optionsBuilder,
        IEnumerable<string> contactPoints,
        string keyspace,
        Action<ScyllaDbContextOptionsBuilder>? scyllaOptionsAction = null)
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);
        ArgumentNullException.ThrowIfNull(contactPoints);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyspace);

        var points = contactPoints.ToArray();

        if (points.Length == 0)
        {
            throw new ArgumentException(ScyllaStrings.ContactPointsRequired(), nameof(contactPoints));
        }

        // A "host:port" contact point reaches the driver as a host name and fails DNS resolution with a message
        // that says nothing about the real mistake. Catch it where the caller can still see what they typed.
        foreach (var point in points)
        {
            if (point.Contains(':', StringComparison.Ordinal) && !point.StartsWith('['))
            {
                throw new ArgumentException(
                    ScyllaStrings.ContactPointMustNotIncludePort(point), nameof(contactPoints));
            }
        }

        return ApplyExtension(
            optionsBuilder,
            e => e.WithContactPoints(points).WithKeyspace(keyspace),
            scyllaOptionsAction);
    }

    /// <inheritdoc cref="UseScyllaDb(DbContextOptionsBuilder, IEnumerable{string}, string, Action{ScyllaDbContextOptionsBuilder})" />
    public static DbContextOptionsBuilder<TContext> UseScyllaDb<TContext>(
        this DbContextOptionsBuilder<TContext> optionsBuilder,
        IEnumerable<string> contactPoints,
        string keyspace,
        Action<ScyllaDbContextOptionsBuilder>? scyllaOptionsAction = null)
        where TContext : DbContext
        => (DbContextOptionsBuilder<TContext>)UseScyllaDb(
            (DbContextOptionsBuilder)optionsBuilder, contactPoints, keyspace, scyllaOptionsAction);

    /// <summary>
    ///     Uses a cluster the caller has already built, so that connection policies the provider does not surface
    ///     — retry policies, load balancing, execution profiles — stay under the caller's control.
    /// </summary>
    public static DbContextOptionsBuilder UseScyllaDb(
        this DbContextOptionsBuilder optionsBuilder,
        ICluster cluster,
        string keyspace,
        Action<ScyllaDbContextOptionsBuilder>? scyllaOptionsAction = null)
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);
        ArgumentNullException.ThrowIfNull(cluster);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyspace);

        return ApplyExtension(
            optionsBuilder,
            e => e.WithCluster(cluster).WithKeyspace(keyspace),
            scyllaOptionsAction);
    }

    /// <summary>
    ///     Uses a session the caller already owns. The provider will not dispose it.
    /// </summary>
    /// <remarks>
    ///     This is also the way to run the provider against a session created by the DataStax
    ///     <c>CassandraCSharpDriver</c>. That package and <c>ScyllaDBCSharpDriver</c> ship different assemblies
    ///     but share the <c>Cassandra</c> namespace, so referencing both requires an <c>extern alias</c>.
    /// </remarks>
    public static DbContextOptionsBuilder UseScyllaDb(
        this DbContextOptionsBuilder optionsBuilder,
        ISession session,
        Action<ScyllaDbContextOptionsBuilder>? scyllaOptionsAction = null)
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);
        ArgumentNullException.ThrowIfNull(session);

        return ApplyExtension(optionsBuilder, e => e.WithSession(session), scyllaOptionsAction);
    }

    private static DbContextOptionsBuilder ApplyExtension(
        DbContextOptionsBuilder optionsBuilder,
        Func<ScyllaOptionsExtension, ScyllaOptionsExtension> configure,
        Action<ScyllaDbContextOptionsBuilder>? scyllaOptionsAction)
    {
        var extension = configure(
            optionsBuilder.Options.FindExtension<ScyllaOptionsExtension>() ?? new ScyllaOptionsExtension());

        ((IDbContextOptionsBuilderInfrastructure)optionsBuilder).AddOrUpdateExtension(extension);

        scyllaOptionsAction?.Invoke(new ScyllaDbContextOptionsBuilder(optionsBuilder));

        return optionsBuilder;
    }
}
