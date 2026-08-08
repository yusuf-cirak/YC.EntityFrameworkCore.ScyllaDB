using Cassandra;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using YC.EntityFrameworkCore.ScyllaDB.Infrastructure.Internal;

namespace YC.EntityFrameworkCore.ScyllaDB.Infrastructure;

/// <summary>
///     Configures ScyllaDB-specific options. Reached through the callback on
///     <c>UseScyllaDb</c>.
/// </summary>
public class ScyllaDbContextOptionsBuilder
{
    public ScyllaDbContextOptionsBuilder(DbContextOptionsBuilder optionsBuilder)
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);

        OptionsBuilder = optionsBuilder;
    }

    protected virtual DbContextOptionsBuilder OptionsBuilder { get; }

    /// <summary>
    ///     The port the cluster listens on. Defaults to 9042.
    /// </summary>
    public virtual ScyllaDbContextOptionsBuilder WithPort(int port)
        => WithOption(e => e.WithPort(port));

    public virtual ScyllaDbContextOptionsBuilder WithCredentials(string userName, string password)
        => WithOption(e => e.WithCredentials(userName, password));

    public virtual ScyllaDbContextOptionsBuilder WithSsl(SSLOptions? sslOptions = null)
        => WithOption(e => e.WithSsl(sslOptions));

    /// <summary>
    ///     Overrides the consistency level for every statement. Left unset, the driver's own default
    ///     (<c>LOCAL_ONE</c>) applies.
    /// </summary>
    public virtual ScyllaDbContextOptionsBuilder WithConsistencyLevel(ConsistencyLevel consistencyLevel)
        => WithOption(e => e.WithConsistencyLevel(consistencyLevel));

    /// <summary>
    ///     Overrides the serial consistency level used by lightweight transactions. Left unset, the driver's
    ///     <c>SERIAL</c> applies.
    /// </summary>
    public virtual ScyllaDbContextOptionsBuilder WithSerialConsistencyLevel(ConsistencyLevel serialConsistencyLevel)
        => WithOption(e => e.WithSerialConsistencyLevel(serialConsistencyLevel));

    public virtual ScyllaDbContextOptionsBuilder WithPageSize(int pageSize)
        => WithOption(e => e.WithPageSize(pageSize));

    public virtual ScyllaDbContextOptionsBuilder WithExecutionProfile(string executionProfile)
        => WithOption(e => e.WithExecutionProfile(executionProfile));

    public virtual ScyllaDbContextOptionsBuilder WithQueryTimeout(TimeSpan queryTimeout)
        => WithOption(e => e.WithQueryTimeout(queryTimeout));

    /// <summary>
    ///     How long to wait for a schema change to reach every node. Defaults to 10 seconds.
    /// </summary>
    public virtual ScyllaDbContextOptionsBuilder WithSchemaAgreementTimeout(TimeSpan schemaAgreementTimeout)
        => WithOption(e => e.WithSchemaAgreementTimeout(schemaAgreementTimeout));

    /// <summary>
    ///     How <c>SaveChangesAsync</c> groups its statements. See <see cref="ScyllaBatchMode" /> — the default
    ///     batches per partition, which is the only cheap batch CQL offers.
    /// </summary>
    public virtual ScyllaDbContextOptionsBuilder WithBatchMode(ScyllaBatchMode batchMode)
        => WithOption(e => e.WithBatchMode(batchMode));

    /// <summary>
    ///     Caps how many statements a cross-partition <c>SaveChangesAsync</c> keeps in flight. Defaults to 32.
    /// </summary>
    public virtual ScyllaDbContextOptionsBuilder WithMaxConcurrentWrites(int maxConcurrentWrites)
        => WithOption(e => e.WithMaxConcurrentWrites(maxConcurrentWrites));

    public virtual ScyllaDbContextOptionsBuilder WithTracing(bool enableTracing = true)
        => WithOption(e => e.WithTracing(enableTracing));

    protected virtual ScyllaDbContextOptionsBuilder WithOption(
        Func<ScyllaOptionsExtension, ScyllaOptionsExtension> setOption)
    {
        ArgumentNullException.ThrowIfNull(setOption);

        ((IDbContextOptionsBuilderInfrastructure)OptionsBuilder).AddOrUpdateExtension(
            setOption(OptionsBuilder.Options.FindExtension<ScyllaOptionsExtension>() ?? new ScyllaOptionsExtension()));

        return this;
    }
}
