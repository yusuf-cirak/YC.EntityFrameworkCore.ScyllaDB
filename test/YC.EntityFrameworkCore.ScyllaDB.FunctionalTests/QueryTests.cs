using Microsoft.EntityFrameworkCore;

namespace YC.EntityFrameworkCore.ScyllaDB.FunctionalTests;

/// <summary>
///     Runs translated queries against a real node.
/// </summary>
[Collection(ScyllaCollection.Name)]
public class QueryTests(ScyllaContainerFixture fixture) : IAsyncLifetime
{
    private const string Keyspace = "query_tests";

    private static readonly Guid SensorA = new("aaaaaaaa-0000-0000-0000-000000000000");
    private static readonly Guid SensorB = new("bbbbbbbb-0000-0000-0000-000000000000");
    private static readonly DateTimeOffset Base = new(2026, 8, 9, 12, 0, 0, TimeSpan.Zero);

    private sealed class Reading
    {
        public Guid SensorId { get; set; }

        public DateTimeOffset RecordedAt { get; set; }

        public double Value { get; set; }

        public string? Status { get; set; }
    }

    private sealed class QueryContext(DbContextOptions options) : DbContext(options)
    {
        public DbSet<Reading> Readings
            => Set<Reading>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.Entity<Reading>(e =>
            {
                e.ToScyllaTable("readings");
                e.HasPartitionKey(r => r.SensorId);
                e.HasClusteringKey(r => r.RecordedAt, ScyllaClusteringOrder.Descending);
            });
    }

    private QueryContext CreateContext()
        => new(
            new DbContextOptionsBuilder<QueryContext>()
                .UseScyllaDb(fixture.ContactPoints, Keyspace, o => o.WithPort(fixture.Port))
                .Options);

    public async ValueTask InitializeAsync()
    {
        await using var context = CreateContext();
        await context.Database.EnsureCreatedAsync();

        for (var i = 0; i < 5; i++)
        {
            context.Add(
                new Reading
                {
                    SensorId = SensorA,
                    RecordedAt = Base.AddMinutes(i),
                    Value = i,
                    Status = i % 2 == 0 ? "ok" : "warn"
                });
        }

        context.Add(new Reading { SensorId = SensorB, RecordedAt = Base, Value = 100, Status = "ok" });

        await context.SaveChangesAsync();
    }

    public ValueTask DisposeAsync()
        => ValueTask.CompletedTask;

    [Fact]
    public async Task Partition_scoped_query_returns_that_partition()
    {
        await using var context = CreateContext();

        var readings = await context.Readings
            .Where(r => r.SensorId == SensorA)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(5, readings.Count);
        Assert.All(readings, r => Assert.Equal(SensorA, r.SensorId));
    }

    [Fact]
    public async Task Materialised_entities_carry_every_column()
    {
        await using var context = CreateContext();

        var reading = await context.Readings
            .Where(r => r.SensorId == SensorB)
            .FirstAsync(TestContext.Current.CancellationToken);

        Assert.Equal(SensorB, reading.SensorId);
        Assert.Equal(Base, reading.RecordedAt);
        Assert.Equal(100, reading.Value);
        Assert.Equal("ok", reading.Status);
    }

    /// <summary>
    ///     Rows come back in the declared clustering order without an explicit <c>OrderBy</c>, because that is
    ///     the order they are stored in.
    /// </summary>
    [Fact]
    public async Task Rows_arrive_in_the_declared_clustering_order()
    {
        await using var context = CreateContext();

        var readings = await context.Readings
            .Where(r => r.SensorId == SensorA)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            readings.Select(r => r.RecordedAt).OrderByDescending(t => t),
            readings.Select(r => r.RecordedAt));
    }

    [Fact]
    public async Task Clustering_range_restriction_is_translated()
    {
        await using var context = CreateContext();

        var readings = await context.Readings
            .Where(r => r.SensorId == SensorA && r.RecordedAt >= Base.AddMinutes(3))
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, readings.Count);
    }

    [Fact]
    public async Task Take_limits_the_rows_read()
    {
        await using var context = CreateContext();

        var readings = await context.Readings
            .Where(r => r.SensorId == SensorA)
            .Take(2)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, readings.Count);
    }

    [Fact]
    public async Task Count_and_Any_are_answered_by_the_server()
    {
        await using var context = CreateContext();

        Assert.Equal(
            5, await context.Readings.CountAsync(r => r.SensorId == SensorA, TestContext.Current.CancellationToken));
        Assert.True(
            await context.Readings.AnyAsync(r => r.SensorId == SensorB, TestContext.Current.CancellationToken));
        Assert.False(
            await context.Readings.AnyAsync(
                r => r.SensorId == Guid.Empty, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task OrderBy_matching_the_reverse_clustering_order_is_translated()
    {
        await using var context = CreateContext();

        var readings = await context.Readings
            .Where(r => r.SensorId == SensorA)
            .OrderBy(r => r.RecordedAt)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            readings.Select(r => r.RecordedAt).OrderBy(t => t),
            readings.Select(r => r.RecordedAt));
    }

    [Fact]
    public async Task Captured_variables_become_query_parameters()
    {
        await using var context = CreateContext();
        var sensorId = SensorA;

        var readings = await context.Readings
            .Where(r => r.SensorId == sensorId)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(5, readings.Count);
    }

    [Fact]
    public async Task Contains_over_partition_keys_becomes_IN()
    {
        await using var context = CreateContext();
        var sensorIds = new[] { SensorA, SensorB };

        var readings = await context.Readings
            .Where(r => sensorIds.Contains(r.SensorId))
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(6, readings.Count);
    }

    [Fact]
    public async Task AllowFiltering_permits_a_non_key_restriction()
    {
        await using var context = CreateContext();

        var readings = await context.Readings
            .Where(r => r.Status == "warn" && EF.Functions.AllowFiltering())
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, readings.Count);
    }

    [Fact]
    public async Task Synchronous_enumeration_is_refused()
    {
        await using var context = CreateContext();

        var exception = Assert.Throws<InvalidOperationException>(
            () => context.Readings.Where(r => r.SensorId == SensorA).ToList());

        Assert.Contains("asynchronous only", exception.Message, StringComparison.Ordinal);
    }
}
