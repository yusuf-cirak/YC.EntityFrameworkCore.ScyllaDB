using Cassandra;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using YC.EntityFrameworkCore.ScyllaDB.Infrastructure.Internal;

namespace YC.EntityFrameworkCore.ScyllaDB.UnitTests.Infrastructure;

public class ScyllaOptionsExtensionTests
{
    private static ScyllaOptionsExtension ExtensionFor(Action<DbContextOptionsBuilder> configure)
    {
        var builder = new DbContextOptionsBuilder();
        configure(builder);
        return builder.Options.FindExtension<ScyllaOptionsExtension>()!;
    }

    [Fact]
    public void UseScyllaDb_records_contact_points_keyspace_and_the_default_port()
    {
        var extension = ExtensionFor(b => b.UseScyllaDb(["10.0.0.1", "10.0.0.2"], "metrics"));

        Assert.Equal(["10.0.0.1", "10.0.0.2"], extension.ContactPoints);
        Assert.Equal("metrics", extension.Keyspace);
        Assert.Equal(9042, extension.Port);
    }

    /// <summary>
    ///     The provider intentionally leaves consistency unset so the driver's own default (LOCAL_ONE) applies.
    ///     Imposing a different default would silently change the durability characteristics of every query.
    /// </summary>
    [Fact]
    public void Consistency_levels_are_unset_unless_configured()
    {
        var extension = ExtensionFor(b => b.UseScyllaDb(["10.0.0.1"], "metrics"));

        Assert.Null(extension.ConsistencyLevel);
        Assert.Null(extension.SerialConsistencyLevel);
    }

    [Fact]
    public void Options_action_configures_the_extension()
    {
        var extension = ExtensionFor(
            b => b.UseScyllaDb(
                ["10.0.0.1"], "metrics", o => o
                    .WithPort(19042)
                    .WithConsistencyLevel(ConsistencyLevel.LocalQuorum)
                    .WithPageSize(250)
                    .WithBatchMode(ScyllaBatchMode.Logged)
                    .WithMaxConcurrentWrites(8)
                    .WithTracing()));

        Assert.Equal(19042, extension.Port);
        Assert.Equal(ConsistencyLevel.LocalQuorum, extension.ConsistencyLevel);
        Assert.Equal(250, extension.PageSize);
        Assert.Equal(ScyllaBatchMode.Logged, extension.BatchMode);
        Assert.Equal(8, extension.MaxConcurrentWrites);
        Assert.True(extension.EnableTracing);
    }

    [Fact]
    public void Defaults_are_per_partition_unlogged_batching()
    {
        var extension = ExtensionFor(b => b.UseScyllaDb(["10.0.0.1"], "metrics"));

        Assert.Equal(ScyllaBatchMode.PerPartitionUnlogged, extension.BatchMode);
        Assert.Equal(TimeSpan.FromSeconds(10), extension.SchemaAgreementTimeout);
    }

    [Fact]
    public void Extension_is_immutable_so_options_can_be_shared()
    {
        var original = new ScyllaOptionsExtension().WithKeyspace("first");

        var modified = original.WithKeyspace("second");

        Assert.Equal("first", original.Keyspace);
        Assert.Equal("second", modified.Keyspace);
        Assert.NotSame(original, modified);
    }

    /// <summary>
    ///     The singleton session lives in EF Core's internal service provider, which is keyed on this hash. Two
    ///     keyspaces sharing a hash would share a session and silently read from the wrong keyspace.
    /// </summary>
    [Fact]
    public void Service_provider_hash_distinguishes_connection_settings()
    {
        int HashFor(Action<DbContextOptionsBuilder> configure)
            => ExtensionFor(configure).Info.GetServiceProviderHashCode();

        var baseline = HashFor(b => b.UseScyllaDb(["10.0.0.1"], "metrics"));

        Assert.NotEqual(baseline, HashFor(b => b.UseScyllaDb(["10.0.0.1"], "other_keyspace")));
        Assert.NotEqual(baseline, HashFor(b => b.UseScyllaDb(["10.0.0.2"], "metrics")));
        Assert.NotEqual(baseline, HashFor(b => b.UseScyllaDb(["10.0.0.1"], "metrics", o => o.WithPort(19042))));
        Assert.Equal(baseline, HashFor(b => b.UseScyllaDb(["10.0.0.1"], "metrics")));
    }

    [Fact]
    public void Extension_declares_itself_the_database_provider()
    {
        var extension = ExtensionFor(b => b.UseScyllaDb(["10.0.0.1"], "metrics"));

        Assert.True(extension.Info.IsDatabaseProvider);
        Assert.Contains("ScyllaDB", extension.Info.LogFragment, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_rejects_an_extension_with_no_connection()
    {
        var options = new DbContextOptionsBuilder().Options;

        var exception = Assert.Throws<InvalidOperationException>(
            () => new ScyllaOptionsExtension().Validate(options));

        Assert.Contains("No ScyllaDB connection was configured", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_requires_a_keyspace_when_no_session_was_supplied()
    {
        var options = new DbContextOptionsBuilder().Options;

        var exception = Assert.Throws<InvalidOperationException>(
            () => new ScyllaOptionsExtension().WithContactPoints(["10.0.0.1"]).Validate(options));

        Assert.Contains("keyspace is required", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     A "host:port" contact point reaches the driver as a host name and fails DNS resolution, which says
    ///     nothing about the actual mistake. Rejecting it up front is the difference between a five-second fix
    ///     and a confusing hunt.
    /// </summary>
    [Fact]
    public void UseScyllaDb_rejects_a_contact_point_that_carries_a_port()
    {
        var exception = Assert.Throws<ArgumentException>(
            () => new DbContextOptionsBuilder().UseScyllaDb(["10.0.0.1:9042"], "metrics"));

        Assert.Contains("includes a port", exception.Message, StringComparison.Ordinal);
        Assert.Contains("WithPort", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UseScyllaDb_rejects_an_empty_contact_point_list()
    {
        var exception = Assert.Throws<ArgumentException>(
            () => new DbContextOptionsBuilder().UseScyllaDb([], "metrics"));

        Assert.Contains("contact point", exception.Message, StringComparison.Ordinal);
    }
}
