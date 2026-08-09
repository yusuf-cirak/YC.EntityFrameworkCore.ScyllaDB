using Cassandra;
using Microsoft.EntityFrameworkCore.Infrastructure;
using YC.EntityFrameworkCore.ScyllaDB.Internal;

namespace YC.EntityFrameworkCore.ScyllaDB.Infrastructure.Internal;

/// <summary>
///     The connection-shaping options, frozen once per internal service provider.
/// </summary>
/// <remarks>
///     Separate from <see cref="ScyllaOptionsExtension" /> because the extension is per-<c>DbContextOptions</c>
///     and mutable by cloning, whereas the singleton session needs a single settled view of the configuration.
/// </remarks>
public interface IScyllaSingletonOptions : ISingletonOptions
{
    IReadOnlyList<string> ContactPoints { get; }

    int Port { get; }

    string Keyspace { get; }

    string? UserName { get; }

    string? Password { get; }

    SSLOptions? SslOptions { get; }

    ConsistencyLevel? ConsistencyLevel { get; }

    ConsistencyLevel? SerialConsistencyLevel { get; }

    int? PageSize { get; }

    string? ExecutionProfile { get; }

    TimeSpan? QueryTimeout { get; }

    TimeSpan SchemaAgreementTimeout { get; }

    ScyllaBatchMode BatchMode { get; }

    int MaxConcurrentWrites { get; }

    bool EnableTracing { get; }

    bool EmitQueryTextInTelemetry { get; }

    ICluster? Cluster { get; }

    ISession? Session { get; }
}

/// <inheritdoc cref="IScyllaSingletonOptions" />
public class ScyllaSingletonOptions : IScyllaSingletonOptions
{
    public virtual IReadOnlyList<string> ContactPoints { get; private set; } = [];

    public virtual int Port { get; private set; } = ProtocolOptions.DefaultPort;

    public virtual string Keyspace { get; private set; } = string.Empty;

    public virtual string? UserName { get; private set; }

    public virtual string? Password { get; private set; }

    public virtual SSLOptions? SslOptions { get; private set; }

    public virtual ConsistencyLevel? ConsistencyLevel { get; private set; }

    public virtual ConsistencyLevel? SerialConsistencyLevel { get; private set; }

    public virtual int? PageSize { get; private set; }

    public virtual string? ExecutionProfile { get; private set; }

    public virtual TimeSpan? QueryTimeout { get; private set; }

    public virtual TimeSpan SchemaAgreementTimeout { get; private set; } = TimeSpan.FromSeconds(10);

    public virtual ScyllaBatchMode BatchMode { get; private set; } = ScyllaBatchMode.PerPartitionUnlogged;

    public virtual int MaxConcurrentWrites { get; private set; } = 32;

    public virtual bool EnableTracing { get; private set; }

    public virtual bool EmitQueryTextInTelemetry { get; private set; } = true;

    public virtual ICluster? Cluster { get; private set; }

    public virtual ISession? Session { get; private set; }

    public virtual void Initialize(IDbContextOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var extension = options.FindExtension<ScyllaOptionsExtension>()
                        ?? throw new InvalidOperationException(ScyllaStrings.NoConnectionConfigured());

        ContactPoints = extension.ContactPoints;
        Port = extension.Port;
        Keyspace = extension.Keyspace ?? extension.Session?.Keyspace ?? string.Empty;
        UserName = extension.UserName;
        Password = extension.Password;
        SslOptions = extension.SslOptions;
        ConsistencyLevel = extension.ConsistencyLevel;
        SerialConsistencyLevel = extension.SerialConsistencyLevel;
        PageSize = extension.PageSize;
        ExecutionProfile = extension.ExecutionProfile;
        QueryTimeout = extension.QueryTimeout;
        SchemaAgreementTimeout = extension.SchemaAgreementTimeout;
        BatchMode = extension.BatchMode;
        MaxConcurrentWrites = extension.MaxConcurrentWrites;
        EnableTracing = extension.EnableTracing;
        EmitQueryTextInTelemetry = extension.EmitQueryTextInTelemetry;
        Cluster = extension.Cluster;
        Session = extension.Session;
    }

    /// <summary>
    ///     Guards against a second <c>DbContext</c> reaching the same internal service provider with different
    ///     connection settings — which would silently hand it the first context's session.
    /// </summary>
    public virtual void Validate(IDbContextOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var extension = options.FindExtension<ScyllaOptionsExtension>();

        if (extension is null)
        {
            return;
        }

        var currentKeyspace = extension.Keyspace ?? extension.Session?.Keyspace ?? string.Empty;

        if (!string.Equals(Keyspace, currentKeyspace, StringComparison.Ordinal)
            || !ContactPoints.SequenceEqual(extension.ContactPoints, StringComparer.Ordinal)
            || Port != extension.Port
            || !ReferenceEquals(Cluster, extension.Cluster)
            || !ReferenceEquals(Session, extension.Session))
        {
            throw new InvalidOperationException(
                "ScyllaDB connection options differ from those the internal service provider was built with. "
                + "This usually means two DbContext configurations share a service provider but point at different "
                + "clusters or keyspaces. Configure each context with its own options, or make the settings match.");
        }
    }
}
