using System.Collections.Concurrent;
using Cassandra;
using YC.EntityFrameworkCore.ScyllaDB.Infrastructure.Internal;

namespace YC.EntityFrameworkCore.ScyllaDB.Storage.Internal;

/// <summary>
///     Owns the cluster and the single session shared by every <c>DbContext</c> built on the same options.
/// </summary>
public interface IScyllaSingletonClientWrapper : IAsyncDisposable
{
    /// <summary>
    ///     Returns the shared session, connecting on first use.
    /// </summary>
    Task<ISession> GetSessionAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     Prepares a statement, reusing an earlier preparation of the same CQL.
    /// </summary>
    Task<PreparedStatement> PrepareAsync(string cql, CancellationToken cancellationToken = default);
}

/// <summary>
///     Singleton holder for the driver's <see cref="ICluster" /> and <see cref="ISession" />.
/// </summary>
/// <remarks>
///     <para>
///         A driver session is thread-safe, pools connections internally, and is meant to live for the lifetime
///         of the application — one per keyspace. That is the opposite of EF Core's usual per-context connection
///         model, which is why the session sits here as a singleton and the scoped
///         <see cref="ScyllaClientWrapper" /> merely borrows it.
///     </para>
///     <para>
///         EF Core keys its internal service provider on
///         <see cref="ScyllaOptionsExtension" />'s service-provider hash code, so "one singleton per distinct
///         configuration" falls out of that mechanism rather than needing a keyed cache here.
///     </para>
/// </remarks>
public class ScyllaSingletonClientWrapper : IScyllaSingletonClientWrapper
{
    private readonly IScyllaSingletonOptions _options;
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private readonly ConcurrentDictionary<string, Task<PreparedStatement>> _preparedStatements =
        new(StringComparer.Ordinal);

    private ICluster? _ownedCluster;
    private ISession? _session;
    private bool _ownsSession;
    private bool _disposed;

    public ScyllaSingletonClientWrapper(IScyllaSingletonOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options;
    }

    public virtual async Task<ISession> GetSessionAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_session is { } existing)
        {
            return existing;
        }

        await _connectLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // A second caller may have connected while this one waited.
            if (_session is { } connected)
            {
                return connected;
            }

            // Deliberately not caching a faulted task: a cluster that is merely slow to start would otherwise
            // poison every later request for the lifetime of the process.
            var session = await ConnectAsync().ConfigureAwait(false);
            _session = session;
            return session;
        }
        finally
        {
            _connectLock.Release();
        }
    }

    public virtual Task<PreparedStatement> PrepareAsync(
        string cql,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cql);

        // The driver de-duplicates PREPARE on the wire and caches server-side; this dictionary only avoids
        // re-awaiting that lookup and re-allocating the Task on a hot path.
        return _preparedStatements.GetOrAdd(
            cql,
            static (text, state) => state.Self.PrepareCoreAsync(text, state.CancellationToken),
            (Self: this, CancellationToken: cancellationToken));
    }

    protected virtual async Task<PreparedStatement> PrepareCoreAsync(string cql, CancellationToken cancellationToken)
    {
        var session = await GetSessionAsync(cancellationToken).ConfigureAwait(false);
        return await session.PrepareAsync(cql).ConfigureAwait(false);
    }

    protected virtual async Task<ISession> ConnectAsync()
    {
        if (_options.Session is { } suppliedSession)
        {
            _ownsSession = false;
            return suppliedSession;
        }

        var cluster = _options.Cluster;

        if (cluster is null)
        {
            cluster = BuildCluster();
            _ownedCluster = cluster;
        }

        _ownsSession = true;

        // Deliberately connected without a keyspace. Binding the session to one would make it impossible to
        // create that keyspace, since connecting would fail before EnsureCreatedAsync could run. Every
        // statement the provider emits is keyspace-qualified instead, which is also unambiguous.
        return await cluster.ConnectAsync().ConfigureAwait(false);
    }

    protected virtual ICluster BuildCluster()
    {
        var builder = Cluster.Builder()
            .AddContactPoints(_options.ContactPoints.ToArray())
            .WithPort(_options.Port);

        if (_options.UserName is { } userName)
        {
            builder = builder.WithCredentials(userName, _options.Password ?? string.Empty);
        }

        if (_options.SslOptions is { } sslOptions)
        {
            builder = builder.WithSSL(sslOptions);
        }

        var queryOptions = new QueryOptions();
        var queryOptionsChanged = false;

        if (_options.ConsistencyLevel is { } consistencyLevel)
        {
            queryOptions = queryOptions.SetConsistencyLevel(consistencyLevel);
            queryOptionsChanged = true;
        }

        if (_options.SerialConsistencyLevel is { } serialConsistencyLevel)
        {
            queryOptions = queryOptions.SetSerialConsistencyLevel(serialConsistencyLevel);
            queryOptionsChanged = true;
        }

        if (_options.PageSize is { } pageSize)
        {
            queryOptions = queryOptions.SetPageSize(pageSize);
            queryOptionsChanged = true;
        }

        if (queryOptionsChanged)
        {
            builder = builder.WithQueryOptions(queryOptions);
        }

        if (_options.QueryTimeout is { } queryTimeout)
        {
            builder = builder.WithSocketOptions(
                new SocketOptions().SetReadTimeoutMillis((int)queryTimeout.TotalMilliseconds));
        }

        return builder.Build();
    }

    public virtual async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // A caller-supplied session or cluster belongs to the caller; only tear down what was built here.
        if (_ownsSession && _session is { } session)
        {
            await session.ShutdownAsync().ConfigureAwait(false);
        }

        if (_ownedCluster is { } cluster)
        {
            await cluster.ShutdownAsync().ConfigureAwait(false);
        }

        _connectLock.Dispose();

        GC.SuppressFinalize(this);
    }
}
