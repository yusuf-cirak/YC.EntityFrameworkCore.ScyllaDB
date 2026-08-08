using Microsoft.EntityFrameworkCore;
using YC.EntityFrameworkCore.ScyllaDB.UnitTests.TestUtilities;

namespace YC.EntityFrameworkCore.ScyllaDB.UnitTests.Metadata;

/// <summary>
///     Asserts the exact CQL the provider emits for a schema. A table's primary key is its physical layout and
///     cannot be altered afterwards, so a silent change here would be unrecoverable in a live keyspace.
/// </summary>
public class DdlGenerationTests
{
    [Fact]
    public void Simple_partition_and_clustering_key_produce_a_create_table()
    {
        var cql = ScyllaModelFixture.CreateTableCql<Reading>(
            b => b.Entity<Reading>(e =>
            {
                e.ToScyllaTable("readings");
                e.HasPartitionKey(r => r.SensorId);
                e.HasClusteringKey(r => r.RecordedAt, ScyllaClusteringOrder.Descending);
            }));

        Assert.Contains("CREATE TABLE IF NOT EXISTS \"test_keyspace\".\"readings\"", cql, StringComparison.Ordinal);
        Assert.Contains("\"SensorId\" uuid", cql, StringComparison.Ordinal);
        Assert.Contains("\"RecordedAt\" timestamp", cql, StringComparison.Ordinal);
        Assert.Contains("\"Value\" double", cql, StringComparison.Ordinal);
        Assert.Contains("PRIMARY KEY ((\"SensorId\"), \"RecordedAt\")", cql, StringComparison.Ordinal);
        Assert.Contains("CLUSTERING ORDER BY (\"RecordedAt\" DESC)", cql, StringComparison.Ordinal);
    }

    /// <summary>
    ///     <c>PRIMARY KEY (a, b)</c> partitions by <c>a</c> and clusters by <c>b</c>, while
    ///     <c>PRIMARY KEY ((a, b))</c> partitions by both. Getting this wrong silently changes how data is
    ///     distributed, so the composite form is pinned explicitly.
    /// </summary>
    [Fact]
    public void Composite_partition_key_is_wrapped_in_its_own_parentheses()
    {
        var cql = ScyllaModelFixture.CreateTableCql<Event>(
            b => b.Entity<Event>(e =>
            {
                e.ToScyllaTable("events");
                e.HasPartitionKey(x => new { x.TenantId, x.Bucket });
                e.HasClusteringKey(x => x.OccurredAt, ScyllaClusteringOrder.Descending);
                e.HasClusteringKey(x => x.EventId);
                e.Property(x => x.TenantName).IsStaticColumn();
            }));

        Assert.Contains(
            "PRIMARY KEY ((\"TenantId\", \"Bucket\"), \"OccurredAt\", \"EventId\")", cql, StringComparison.Ordinal);
        Assert.Contains("\"TenantName\" text STATIC", cql, StringComparison.Ordinal);
        Assert.Contains(
            "CLUSTERING ORDER BY (\"OccurredAt\" DESC, \"EventId\" ASC)", cql, StringComparison.Ordinal);
    }

    [Fact]
    public void Table_with_no_clustering_key_emits_no_clustering_order()
    {
        var cql = ScyllaModelFixture.CreateTableCql<Reading>(
            b => b.Entity<Reading>(e =>
            {
                e.ToScyllaTable("readings");
                e.HasPartitionKey(r => r.SensorId);
            }));

        Assert.Contains("PRIMARY KEY ((\"SensorId\"))", cql, StringComparison.Ordinal);
        Assert.DoesNotContain("CLUSTERING ORDER BY", cql, StringComparison.Ordinal);
    }

    [Fact]
    public void Default_time_to_live_is_emitted_in_seconds()
    {
        var cql = ScyllaModelFixture.CreateTableCql<Reading>(
            b => b.Entity<Reading>(e =>
            {
                e.ToScyllaTable("readings");
                e.HasPartitionKey(r => r.SensorId);
                e.HasClusteringKey(r => r.RecordedAt);
                e.HasDefaultTimeToLive(TimeSpan.FromHours(2));
            }));

        Assert.Contains("default_time_to_live = 7200", cql, StringComparison.Ordinal);
    }

    [Fact]
    public void Counter_table_maps_the_counter_column_to_the_counter_type()
    {
        var cql = ScyllaModelFixture.CreateTableCql<PageView>(
            b => b.Entity<PageView>(e =>
            {
                e.ToScyllaTable("page_views");
                e.HasPartitionKey(p => p.Path);
                e.Property(p => p.Views).IsCounter();
            }));

        Assert.Contains("\"Views\" counter", cql, StringComparison.Ordinal);
    }

    [Fact]
    public void Explicit_cql_type_overrides_the_clr_mapping()
    {
        var cql = ScyllaModelFixture.CreateTableCql<Reading>(
            b => b.Entity<Reading>(e =>
            {
                e.ToScyllaTable("readings");
                e.HasPartitionKey(r => r.SensorId);
                e.Property(r => r.Status).HasCqlType("ascii");
            }));

        Assert.Contains("\"Status\" ascii", cql, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Unquoted CQL identifiers fold to lower case, which would turn a <c>SensorId</c> property into a
    ///     <c>sensorid</c> column and break any hand-written CQL that quoted the name.
    /// </summary>
    [Fact]
    public void Identifiers_are_quoted_so_casing_survives()
    {
        var cql = ScyllaModelFixture.CreateTableCql<Reading>(
            b => b.Entity<Reading>(e =>
            {
                e.ToScyllaTable("readings");
                e.HasPartitionKey(r => r.SensorId);
            }));

        Assert.DoesNotContain(" SensorId ", cql, StringComparison.Ordinal);
        Assert.Contains("\"SensorId\"", cql, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_keyspace_defaults_to_a_single_replica()
    {
        var cql = ScyllaModelFixture.CreateKeyspaceCql(
            b => b.Entity<Reading>(e =>
            {
                e.ToScyllaTable("readings");
                e.HasPartitionKey(r => r.SensorId);
            }));

        Assert.Contains("CREATE KEYSPACE IF NOT EXISTS \"test_keyspace\"", cql, StringComparison.Ordinal);
        Assert.Contains("'class': 'SimpleStrategy'", cql, StringComparison.Ordinal);
        Assert.Contains("'replication_factor': 1", cql, StringComparison.Ordinal);
    }

    [Fact]
    public void Network_topology_replication_lists_every_data_centre()
    {
        var cql = ScyllaModelFixture.CreateKeyspaceCql(
            b =>
            {
                b.HasKeyspaceReplication(ScyllaReplication.NetworkTopologyStrategy(("dc1", 3), ("dc2", 2)));
                b.Entity<Reading>(e =>
                {
                    e.ToScyllaTable("readings");
                    e.HasPartitionKey(r => r.SensorId);
                });
            });

        Assert.Contains("'class': 'NetworkTopologyStrategy'", cql, StringComparison.Ordinal);
        Assert.Contains("'dc1': 3", cql, StringComparison.Ordinal);
        Assert.Contains("'dc2': 2", cql, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The attribute and fluent styles must describe the same table, or one of them is a trap.
    /// </summary>
    [Fact]
    public void Attributes_and_fluent_configuration_produce_the_same_table()
    {
        var fluent = ScyllaModelFixture.CreateTableCql<Reading>(
            b => b.Entity<Reading>(e =>
            {
                e.ToScyllaTable("readings");
                e.HasPartitionKey(r => r.SensorId);
                e.HasClusteringKey(r => r.RecordedAt, ScyllaClusteringOrder.Descending);
                e.Ignore(r => r.Status);
            }));

        var attributed = ScyllaModelFixture.CreateTableCql<AttributedReading>(b => b.Entity<AttributedReading>());

        Assert.Equal(
            fluent.Replace(nameof(Reading), nameof(AttributedReading), StringComparison.Ordinal),
            attributed);
    }
}
