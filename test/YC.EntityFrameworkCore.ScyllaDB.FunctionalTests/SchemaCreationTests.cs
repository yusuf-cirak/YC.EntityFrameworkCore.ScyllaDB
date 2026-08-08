using Cassandra;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using YC.EntityFrameworkCore.ScyllaDB.Storage.Internal;

namespace YC.EntityFrameworkCore.ScyllaDB.FunctionalTests;

/// <summary>
///     Creates a real schema and reads it back out of <c>system_schema</c>.
/// </summary>
/// <remarks>
///     Unit tests pin the CQL text; only ScyllaDB itself can confirm that text means what the model intended.
///     The column kinds and clustering order below are the parts that cannot be changed after the fact, so they
///     are checked against the server's own view rather than against the statement we sent.
/// </remarks>
[Collection(ScyllaCollection.Name)]
public class SchemaCreationTests(ScyllaContainerFixture fixture)
{
    private sealed class Reading
    {
        public Guid SensorId { get; set; }

        public string Bucket { get; set; } = string.Empty;

        public DateTimeOffset RecordedAt { get; set; }

        public double Value { get; set; }

        public string SensorName { get; set; } = string.Empty;
    }

    private sealed class MetricsContext(DbContextOptions options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.Entity<Reading>(e =>
            {
                e.ToScyllaTable("readings");
                e.HasPartitionKey(r => new { r.SensorId, r.Bucket });
                e.HasClusteringKey(r => r.RecordedAt, ScyllaClusteringOrder.Descending);
                e.Property(r => r.SensorName).IsStaticColumn();
            });
    }

    private MetricsContext CreateContext(string keyspace)
        => new(
            new DbContextOptionsBuilder<MetricsContext>()
                .UseScyllaDb(fixture.ContactPoints, keyspace, o => o.WithPort(fixture.Port))
                .Options);

    [Fact]
    public async Task EnsureCreatedAsync_creates_the_keyspace_and_its_tables()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var context = CreateContext("schema_creation_tests");

        var created = await context.Database.EnsureCreatedAsync(cancellationToken);

        Assert.True(created);
        Assert.True(await context.Database.CanConnectAsync(cancellationToken));

        var client = context.GetService<IScyllaClientWrapper>();

        var tables = await client.ExecuteAsync(
            new SimpleStatement(
                "SELECT table_name FROM system_schema.tables WHERE keyspace_name = ?", "schema_creation_tests"),
            cancellationToken);

        Assert.Contains("readings", tables.Select(r => r.GetValue<string>("table_name")));
    }

    /// <summary>
    ///     Column kind and clustering order are physical layout: they decide which queries are possible and
    ///     cannot be altered once the table exists.
    /// </summary>
    [Fact]
    public async Task Created_table_has_the_partition_clustering_and_static_columns_the_model_declared()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var context = CreateContext("schema_columns_tests");

        await context.Database.EnsureCreatedAsync(cancellationToken);

        var client = context.GetService<IScyllaClientWrapper>();

        var rows = await client.ExecuteAsync(
            new SimpleStatement(
                "SELECT column_name, kind, position, clustering_order, type FROM system_schema.columns "
                + "WHERE keyspace_name = ? AND table_name = ?",
                "schema_columns_tests",
                "readings"),
            cancellationToken);

        var columns = rows.ToDictionary(
            r => r.GetValue<string>("column_name"),
            r => (Kind: r.GetValue<string>("kind"),
                Position: r.GetValue<int>("position"),
                ClusteringOrder: r.GetValue<string>("clustering_order"),
                Type: r.GetValue<string>("type")),
            StringComparer.Ordinal);

        Assert.Equal("partition_key", columns["SensorId"].Kind);
        Assert.Equal(0, columns["SensorId"].Position);
        Assert.Equal("uuid", columns["SensorId"].Type);

        Assert.Equal("partition_key", columns["Bucket"].Kind);
        Assert.Equal(1, columns["Bucket"].Position);

        Assert.Equal("clustering", columns["RecordedAt"].Kind);
        // Scylla reports this as "DESC"; Cassandra's own documentation shows "desc". Compare without case so
        // the test does not depend on which of the two is behind the driver.
        Assert.Equal("desc", columns["RecordedAt"].ClusteringOrder, StringComparer.OrdinalIgnoreCase);
        Assert.Equal("timestamp", columns["RecordedAt"].Type);

        Assert.Equal("static", columns["SensorName"].Kind);
        Assert.Equal("regular", columns["Value"].Kind);
        Assert.Equal("double", columns["Value"].Type);
    }

    [Fact]
    public async Task EnsureCreatedAsync_is_idempotent()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var context = CreateContext("schema_idempotence_tests");

        Assert.True(await context.Database.EnsureCreatedAsync(cancellationToken));
        Assert.False(await context.Database.EnsureCreatedAsync(cancellationToken));
    }

    [Fact]
    public async Task EnsureDeletedAsync_drops_the_keyspace_and_reports_whether_it_existed()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var context = CreateContext("schema_deletion_tests");

        await context.Database.EnsureCreatedAsync(cancellationToken);

        Assert.True(await context.Database.EnsureDeletedAsync(cancellationToken));
        Assert.False(await context.Database.CanConnectAsync(cancellationToken));
        Assert.False(await context.Database.EnsureDeletedAsync(cancellationToken));
    }
}
