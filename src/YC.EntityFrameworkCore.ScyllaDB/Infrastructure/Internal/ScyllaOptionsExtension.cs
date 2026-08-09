using System.Globalization;
using Cassandra;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using YC.EntityFrameworkCore.ScyllaDB.Internal;

namespace YC.EntityFrameworkCore.ScyllaDB.Infrastructure.Internal;

/// <summary>
///     Carries every ScyllaDB-specific option on <see cref="Microsoft.EntityFrameworkCore.DbContextOptions" />.
/// </summary>
/// <remarks>
///     Immutable, in EF Core's usual style: each <c>With*</c> method clones and returns a new instance, so that
///     options objects can be shared safely across contexts.
/// </remarks>
public class ScyllaOptionsExtension : IDbContextOptionsExtension
{
    private DbContextOptionsExtensionInfo? _info;

    public ScyllaOptionsExtension()
    {
    }

    protected ScyllaOptionsExtension(ScyllaOptionsExtension copyFrom)
    {
        ArgumentNullException.ThrowIfNull(copyFrom);

        ContactPoints = copyFrom.ContactPoints;
        Port = copyFrom.Port;
        Keyspace = copyFrom.Keyspace;
        UserName = copyFrom.UserName;
        Password = copyFrom.Password;
        SslOptions = copyFrom.SslOptions;
        ConsistencyLevel = copyFrom.ConsistencyLevel;
        SerialConsistencyLevel = copyFrom.SerialConsistencyLevel;
        PageSize = copyFrom.PageSize;
        ExecutionProfile = copyFrom.ExecutionProfile;
        QueryTimeout = copyFrom.QueryTimeout;
        SchemaAgreementTimeout = copyFrom.SchemaAgreementTimeout;
        BatchMode = copyFrom.BatchMode;
        MaxConcurrentWrites = copyFrom.MaxConcurrentWrites;
        EnableTracing = copyFrom.EnableTracing;
        EmitQueryTextInTelemetry = copyFrom.EmitQueryTextInTelemetry;
        Cluster = copyFrom.Cluster;
        Session = copyFrom.Session;
    }

    public virtual DbContextOptionsExtensionInfo Info
        => _info ??= new ExtensionInfo(this);

    public virtual IReadOnlyList<string> ContactPoints { get; private set; } = [];

    public virtual int Port { get; private set; } = ProtocolOptions.DefaultPort;

    public virtual string? Keyspace { get; private set; }

    public virtual string? UserName { get; private set; }

    public virtual string? Password { get; private set; }

    public virtual SSLOptions? SslOptions { get; private set; }

    /// <summary>
    ///     Left unset by default, so the driver's own default (<c>LOCAL_ONE</c>) applies. The provider
    ///     deliberately does not impose one of its own.
    /// </summary>
    public virtual ConsistencyLevel? ConsistencyLevel { get; private set; }

    /// <summary>
    ///     Serial consistency for lightweight transactions. Unset by default, so the driver's <c>SERIAL</c> applies.
    /// </summary>
    public virtual ConsistencyLevel? SerialConsistencyLevel { get; private set; }

    public virtual int? PageSize { get; private set; }

    public virtual string? ExecutionProfile { get; private set; }

    public virtual TimeSpan? QueryTimeout { get; private set; }

    /// <summary>
    ///     How long to wait for a schema change to propagate to every node before giving up. Schema changes in
    ///     ScyllaDB are eventually consistent, so DDL has to wait for agreement before dependent statements run.
    /// </summary>
    public virtual TimeSpan SchemaAgreementTimeout { get; private set; } = TimeSpan.FromSeconds(10);

    public virtual ScyllaBatchMode BatchMode { get; private set; } = ScyllaBatchMode.PerPartitionUnlogged;

    /// <summary>
    ///     Caps how many statements a single cross-partition <c>SaveChangesAsync</c> has in flight at once.
    /// </summary>
    public virtual int MaxConcurrentWrites { get; private set; } = 32;

    public virtual bool EnableTracing { get; private set; }

    /// <summary>
    ///     Whether the CQL text is attached to telemetry spans as <c>db.query.text</c>.
    /// </summary>
    /// <remarks>
    ///     On by default. Every value the provider sends goes through a bind marker, so the text carries no
    ///     literals — what it does expose is keyspace, table and column names, which is the single most useful
    ///     field for explaining why a query is slow.
    /// </remarks>
    public virtual bool EmitQueryTextInTelemetry { get; private set; } = true;

    /// <summary>
    ///     A cluster supplied by the caller. When set, the provider connects sessions from it rather than
    ///     building its own, and never disposes it.
    /// </summary>
    public virtual ICluster? Cluster { get; private set; }

    /// <summary>
    ///     A session supplied by the caller. When set, the provider uses it as-is and never disposes it — the
    ///     escape hatch for callers who already own their cluster lifecycle.
    /// </summary>
    public virtual ISession? Session { get; private set; }

    protected virtual ScyllaOptionsExtension Clone()
        => new(this);

    public virtual ScyllaOptionsExtension WithContactPoints(IEnumerable<string> contactPoints)
    {
        ArgumentNullException.ThrowIfNull(contactPoints);

        var clone = Clone();
        clone.ContactPoints = [.. contactPoints];
        return clone;
    }

    public virtual ScyllaOptionsExtension WithPort(int port)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(port);

        var clone = Clone();
        clone.Port = port;
        return clone;
    }

    public virtual ScyllaOptionsExtension WithKeyspace(string keyspace)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyspace);

        var clone = Clone();
        clone.Keyspace = keyspace;
        return clone;
    }

    public virtual ScyllaOptionsExtension WithCredentials(string userName, string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userName);
        ArgumentNullException.ThrowIfNull(password);

        var clone = Clone();
        clone.UserName = userName;
        clone.Password = password;
        return clone;
    }

    public virtual ScyllaOptionsExtension WithSsl(SSLOptions? sslOptions = null)
    {
        var clone = Clone();
        clone.SslOptions = sslOptions ?? new SSLOptions();
        return clone;
    }

    public virtual ScyllaOptionsExtension WithConsistencyLevel(ConsistencyLevel consistencyLevel)
    {
        var clone = Clone();
        clone.ConsistencyLevel = consistencyLevel;
        return clone;
    }

    public virtual ScyllaOptionsExtension WithSerialConsistencyLevel(ConsistencyLevel serialConsistencyLevel)
    {
        var clone = Clone();
        clone.SerialConsistencyLevel = serialConsistencyLevel;
        return clone;
    }

    public virtual ScyllaOptionsExtension WithPageSize(int pageSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageSize);

        var clone = Clone();
        clone.PageSize = pageSize;
        return clone;
    }

    public virtual ScyllaOptionsExtension WithExecutionProfile(string executionProfile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executionProfile);

        var clone = Clone();
        clone.ExecutionProfile = executionProfile;
        return clone;
    }

    public virtual ScyllaOptionsExtension WithQueryTimeout(TimeSpan queryTimeout)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(queryTimeout, TimeSpan.Zero);

        var clone = Clone();
        clone.QueryTimeout = queryTimeout;
        return clone;
    }

    public virtual ScyllaOptionsExtension WithSchemaAgreementTimeout(TimeSpan schemaAgreementTimeout)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(schemaAgreementTimeout, TimeSpan.Zero);

        var clone = Clone();
        clone.SchemaAgreementTimeout = schemaAgreementTimeout;
        return clone;
    }

    public virtual ScyllaOptionsExtension WithBatchMode(ScyllaBatchMode batchMode)
    {
        var clone = Clone();
        clone.BatchMode = batchMode;
        return clone;
    }

    public virtual ScyllaOptionsExtension WithMaxConcurrentWrites(int maxConcurrentWrites)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxConcurrentWrites);

        var clone = Clone();
        clone.MaxConcurrentWrites = maxConcurrentWrites;
        return clone;
    }

    public virtual ScyllaOptionsExtension WithTracing(bool enableTracing = true)
    {
        var clone = Clone();
        clone.EnableTracing = enableTracing;
        return clone;
    }

    public virtual ScyllaOptionsExtension WithQueryTextInTelemetry(bool emitQueryText)
    {
        var clone = Clone();
        clone.EmitQueryTextInTelemetry = emitQueryText;
        return clone;
    }

    public virtual ScyllaOptionsExtension WithCluster(ICluster cluster)
    {
        ArgumentNullException.ThrowIfNull(cluster);

        var clone = Clone();
        clone.Cluster = cluster;
        return clone;
    }

    public virtual ScyllaOptionsExtension WithSession(ISession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        var clone = Clone();
        clone.Session = session;
        clone.Keyspace = session.Keyspace ?? clone.Keyspace;
        return clone;
    }

    public virtual void ApplyServices(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddEntityFrameworkScyllaDb();
    }

    public virtual void Validate(IDbContextOptions options)
    {
        if (Session is null && Cluster is null && ContactPoints.Count == 0)
        {
            throw new InvalidOperationException(ScyllaStrings.NoConnectionConfigured());
        }

        if (Session is null && string.IsNullOrWhiteSpace(Keyspace))
        {
            throw new InvalidOperationException(ScyllaStrings.KeyspaceRequired());
        }
    }

    private sealed class ExtensionInfo(ScyllaOptionsExtension extension)
        : DbContextOptionsExtensionInfo(extension)
    {
        private new ScyllaOptionsExtension Extension
            => (ScyllaOptionsExtension)base.Extension;

        public override bool IsDatabaseProvider
            => true;

        public override string LogFragment
            => $"using ScyllaDB (keyspace={Extension.Keyspace}) ";

        /// <summary>
        ///     EF Core keys its internal service provider on this, and the singleton session lives in that
        ///     provider. Folding every connection-shaping value in here is therefore what gives one session per
        ///     distinct configuration — which is exactly the "one session per keyspace per application" rule the
        ///     driver expects. A value omitted here would silently share a session across configurations.
        /// </summary>
        public override int GetServiceProviderHashCode()
        {
            var hash = new HashCode();

            foreach (var contactPoint in Extension.ContactPoints)
            {
                hash.Add(contactPoint, StringComparer.Ordinal);
            }

            hash.Add(Extension.Port);
            hash.Add(Extension.Keyspace, StringComparer.Ordinal);
            hash.Add(Extension.UserName, StringComparer.Ordinal);
            hash.Add(Extension.Password, StringComparer.Ordinal);
            hash.Add(Extension.SslOptions is not null);
            hash.Add(Extension.ConsistencyLevel);
            hash.Add(Extension.SerialConsistencyLevel);
            hash.Add(Extension.PageSize);
            hash.Add(Extension.ExecutionProfile, StringComparer.Ordinal);
            hash.Add(Extension.QueryTimeout);
            hash.Add(Extension.SchemaAgreementTimeout);
            hash.Add(Extension.BatchMode);
            hash.Add(Extension.MaxConcurrentWrites);
            hash.Add(Extension.EnableTracing);
            hash.Add(Extension.EmitQueryTextInTelemetry);
            hash.Add(Extension.Cluster);
            hash.Add(Extension.Session);

            return hash.ToHashCode();
        }

        public override bool ShouldUseSameServiceProvider(DbContextOptionsExtensionInfo other)
            => other is ExtensionInfo otherInfo
               && GetServiceProviderHashCode() == otherInfo.GetServiceProviderHashCode();

        public override void PopulateDebugInfo(IDictionary<string, string> debugInfo)
        {
            ArgumentNullException.ThrowIfNull(debugInfo);

            debugInfo["ScyllaDb:" + nameof(ScyllaOptionsExtension.ContactPoints)] =
                string.Join(",", Extension.ContactPoints);
            debugInfo["ScyllaDb:" + nameof(ScyllaOptionsExtension.Port)] =
                Extension.Port.ToString(CultureInfo.InvariantCulture);
            debugInfo["ScyllaDb:" + nameof(ScyllaOptionsExtension.Keyspace)] =
                Extension.Keyspace ?? string.Empty;
            debugInfo["ScyllaDb:" + nameof(ScyllaOptionsExtension.BatchMode)] =
                Extension.BatchMode.ToString();
        }
    }
}
