using Microsoft.EntityFrameworkCore;
using YC.EntityFrameworkCore.ScyllaDB.Scaffolding;
using YC.EntityFrameworkCore.ScyllaDB.Storage.Internal;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace YC.EntityFrameworkCore.ScyllaDB.FunctionalTests;

/// <summary>
///     Materialized views and schema scaffolding against a real node.
/// </summary>
[Collection(ScyllaCollection.Name)]
public class MaterializedViewAndScaffoldingTests(ScyllaContainerFixture fixture) : IAsyncLifetime
{
    private const string Keyspace = "views_and_scaffolding_tests";

    private static readonly Guid SensorA = new("cccccccc-1111-1111-1111-111111111111");

    private sealed class Reading
    {
        public Guid SensorId { get; set; }

        public DateTimeOffset RecordedAt { get; set; }

        public string Status { get; set; } = string.Empty;

        public double Value { get; set; }
    }

    /// <summary>
    ///     The same rows keyed by status instead of sensor, which is what a materialized view is for.
    /// </summary>
    private sealed class ReadingByStatus
    {
        public Guid SensorId { get; set; }

        public DateTimeOffset RecordedAt { get; set; }

        public string Status { get; set; } = string.Empty;

        public double Value { get; set; }
    }

    private sealed class ViewContext(DbContextOptions options) : DbContext(options)
    {
        public DbSet<Reading> Readings
            => Set<Reading>();

        public DbSet<ReadingByStatus> ReadingsByStatus
            => Set<ReadingByStatus>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Reading>(e =>
            {
                e.ToScyllaTable("readings");
                e.HasPartitionKey(r => r.SensorId);
                e.HasClusteringKey(r => r.RecordedAt);
            });

            modelBuilder.Entity<ReadingByStatus>(e =>
            {
                e.ToMaterializedView("readings_by_status", "readings");
                e.HasPartitionKey(r => r.Status);
                e.HasClusteringKey(r => r.SensorId);
                e.HasClusteringKey(r => r.RecordedAt, ScyllaClusteringOrder.Descending);
            });
        }
    }

    private ViewContext CreateContext()
        => new(
            new DbContextOptionsBuilder<ViewContext>()
                .UseScyllaDb(fixture.ContactPoints, Keyspace, o => o.WithPort(fixture.Port))
                .Options);

    public async ValueTask InitializeAsync()
    {
        await using var context = CreateContext();
        await context.Database.EnsureCreatedAsync();

        for (var i = 0; i < 4; i++)
        {
            context.Add(
                new Reading
                {
                    SensorId = SensorA,
                    RecordedAt = new DateTimeOffset(2026, 8, 9, 12, i, 0, TimeSpan.Zero),
                    Status = i == 2 ? "alarm" : "ok",
                    Value = i
                });
        }

        await context.SaveChangesAsync();
    }

    public ValueTask DisposeAsync()
        => ValueTask.CompletedTask;

    /// <summary>
    ///     The view is keyed by status, so a query the base table could not answer without a scan becomes a
    ///     partition seek.
    /// </summary>
    [Fact]
    public async Task View_is_queryable_by_its_own_partition_key()
    {
        await using var context = CreateContext();

        var alarms = await context.ReadingsByStatus
            .Where(r => r.Status == "alarm")
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Single(alarms);
        Assert.Equal(2d, alarms[0].Value);
        Assert.Equal(SensorA, alarms[0].SensorId);
    }

    [Fact]
    public async Task View_reflects_writes_to_its_base_table()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var context = CreateContext();

        var sensorId = Guid.NewGuid();
        context.Add(
            new Reading
            {
                SensorId = sensorId,
                RecordedAt = new DateTimeOffset(2026, 8, 9, 13, 0, 0, TimeSpan.Zero),
                Status = "critical",
                Value = 99
            });
        await context.SaveChangesAsync(cancellationToken);

        var critical = await context.ReadingsByStatus
            .Where(r => r.Status == "critical")
            .ToListAsync(cancellationToken);

        Assert.Single(critical);
        Assert.Equal(99d, critical[0].Value);
    }

    [Fact]
    public async Task Writing_to_a_view_is_refused()
    {
        await using var context = CreateContext();

        context.Add(
            new ReadingByStatus
            {
                Status = "ok",
                SensorId = Guid.NewGuid(),
                RecordedAt = DateTimeOffset.UnixEpoch,
                Value = 1
            });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => context.SaveChangesAsync(TestContext.Current.CancellationToken));

        Assert.Contains("will not accept writes", exception.Message, StringComparison.Ordinal);
        Assert.Contains("base table", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Scaffolding round-trip: the generated source must describe the schema it was read from, including
    ///     the parts EF Core's own relational scaffolding cannot express.
    /// </summary>
    [Fact]
    public async Task Scaffolding_describes_the_keys_and_the_view()
    {
        await using var context = CreateContext();
        var session = await context.GetService<IScyllaClientWrapper>()
            .GetSessionAsync(TestContext.Current.CancellationToken);

        var source = await ScyllaScaffolder.GenerateAsync(
            session,
            Keyspace,
            new ScyllaScaffoldingOptions { Namespace = "Scaffolded", ContextName = "ReadingsContext" },
            TestContext.Current.CancellationToken);

        Assert.Contains("namespace Scaffolded;", source, StringComparison.Ordinal);
        Assert.Contains("public class Reading", source, StringComparison.Ordinal);
        Assert.Contains("public class ReadingsByStatu", source, StringComparison.Ordinal);

        Assert.Contains("e.ToScyllaTable(\"readings\");", source, StringComparison.Ordinal);
        Assert.Contains("e.HasPartitionKey(x => x.SensorId);", source, StringComparison.Ordinal);
        Assert.Contains("e.HasClusteringKey(x => x.RecordedAt);", source, StringComparison.Ordinal);

        Assert.Contains(
            "e.ToMaterializedView(\"readings_by_status\", \"readings\");", source, StringComparison.Ordinal);
        Assert.Contains(
            "e.HasClusteringKey(x => x.RecordedAt, ScyllaClusteringOrder.Descending);",
            source,
            StringComparison.Ordinal);

        Assert.Contains("public Guid SensorId { get; set; }", source, StringComparison.Ordinal);
        Assert.Contains("public double Value { get; set; }", source, StringComparison.Ordinal);
        Assert.Contains("modelBuilder.HasDefaultKeyspace(\"" + Keyspace + "\");", source, StringComparison.Ordinal);
    }
}
