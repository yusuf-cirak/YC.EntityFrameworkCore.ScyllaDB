using Cassandra;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace YC.EntityFrameworkCore.ScyllaDB.FunctionalTests;

/// <summary>
///     Runs a single ScyllaDB node for the whole test assembly.
/// </summary>
/// <remarks>
///     Starting a node takes the better part of a minute, so it is shared through
///     <see cref="ScyllaCollection" /> and isolation comes from a keyspace per test class instead.
/// </remarks>
public sealed class ScyllaContainerFixture : IAsyncLifetime
{
    private readonly IContainer _container = new ContainerBuilder("scylladb/scylla:6.2")
        // A test node has no business reserving all the host's cores or locking memory.
        .WithCommand("--smp", "1", "--memory", "1G", "--overprovisioned", "1", "--developer-mode", "1")
        .WithPortBinding(9042, assignRandomHostPort: true)
        // Scylla accepts TCP well before it will answer CQL, so waiting on the port alone races. Waiting for
        // cqlsh to return a row is the only reliable signal that the node is actually serving.
        .WithWaitStrategy(
            Wait.ForUnixContainer()
                .UntilCommandIsCompleted("cqlsh", "-e", "SELECT release_version FROM system.local"))
        .Build();

    private ICluster? _cluster;

    public string Host
        => _container.Hostname;

    public int Port
        => _container.GetMappedPublicPort(9042);

    public IReadOnlyList<string> ContactPoints
        => [Host];

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();

        _cluster = Cluster.Builder()
            .AddContactPoint(Host)
            .WithPort(Port)
            .Build();
    }

    /// <summary>
    ///     Creates an empty keyspace for one test class to work in.
    /// </summary>
    public async Task<string> CreateKeyspaceAsync(string name, CancellationToken cancellationToken = default)
    {
        var session = await _cluster!.ConnectAsync().ConfigureAwait(false);

        await session.ExecuteAsync(
                new SimpleStatement(
                    $"CREATE KEYSPACE IF NOT EXISTS {name} "
                    + "WITH replication = {'class': 'SimpleStrategy', 'replication_factor': 1}"))
            .ConfigureAwait(false);

        return name;
    }

    public async ValueTask DisposeAsync()
    {
        if (_cluster is not null)
        {
            await _cluster.ShutdownAsync().ConfigureAwait(false);
        }

        await _container.DisposeAsync();
    }
}

[CollectionDefinition(Name)]
public sealed class ScyllaCollection : ICollectionFixture<ScyllaContainerFixture>
{
    public const string Name = "ScyllaDB";
}
