using YC.EntityFrameworkCore.ScyllaDB;

// ReSharper disable once CheckNamespace
namespace Microsoft.EntityFrameworkCore;

/// <summary>
///     Model-wide ScyllaDB configuration.
/// </summary>
public static class ScyllaModelBuilderExtensions
{
    /// <summary>
    ///     Sets the keyspace entity types use unless they name one of their own.
    /// </summary>
    public static ModelBuilder HasDefaultKeyspace(this ModelBuilder modelBuilder, string keyspace)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyspace);

        modelBuilder.Model.SetDefaultKeyspace(keyspace);

        return modelBuilder;
    }

    /// <summary>
    ///     Sets the replication strategy used when the provider creates the keyspace.
    /// </summary>
    /// <remarks>
    ///     Only applied at creation. Changing an existing keyspace's replication requires a repair across the
    ///     cluster, so the provider will not do it implicitly.
    /// </remarks>
    public static ModelBuilder HasKeyspaceReplication(
        this ModelBuilder modelBuilder,
        ScyllaReplication replication)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        ArgumentNullException.ThrowIfNull(replication);

        modelBuilder.Model.SetReplication(replication);

        return modelBuilder;
    }

    /// <summary>
    ///     Whether writes to this keyspace go through the commit log. Leave this on unless you can afford to
    ///     lose recent writes on an unclean shutdown.
    /// </summary>
    public static ModelBuilder HasDurableWrites(this ModelBuilder modelBuilder, bool durableWrites = true)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Model.SetDurableWrites(durableWrites);

        return modelBuilder;
    }
}
