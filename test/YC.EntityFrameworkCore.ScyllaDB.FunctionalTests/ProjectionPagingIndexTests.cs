using Microsoft.EntityFrameworkCore;

namespace YC.EntityFrameworkCore.ScyllaDB.FunctionalTests;

/// <summary>
///     Projections, cursor paging and secondary indexes against a real node.
/// </summary>
[Collection(ScyllaCollection.Name)]
public class ProjectionPagingIndexTests(ScyllaContainerFixture fixture) : IAsyncLifetime
{
    private const string Keyspace = "ergonomics_tests";

    private static readonly Guid SensorA = new("aaaaaaaa-1111-1111-1111-111111111111");
    private static readonly DateTimeOffset Base = new(2026, 8, 9, 12, 0, 0, TimeSpan.Zero);

    private sealed class Reading
    {
        public Guid SensorId { get; set; }

        public DateTimeOffset RecordedAt { get; set; }

        public double Value { get; set; }

        public string Status { get; set; } = string.Empty;
    }

    private sealed class ErgonomicsContext(DbContextOptions options) : DbContext(options)
    {
        public DbSet<Reading> Readings
            => Set<Reading>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.Entity<Reading>(e =>
            {
                e.ToScyllaTable("readings");
                e.HasPartitionKey(r => r.SensorId);
                e.HasClusteringKey(r => r.RecordedAt);
                e.HasScyllaIndex(r => r.Status, "readings_by_status");
            });
    }

    private ErgonomicsContext CreateContext()
        => new(
            new DbContextOptionsBuilder<ErgonomicsContext>()
                .UseScyllaDb(fixture.ContactPoints, Keyspace, o => o.WithPort(fixture.Port))
                .Options);

    public async ValueTask InitializeAsync()
    {
        await using var context = CreateContext();
        await context.Database.EnsureCreatedAsync();

        for (var i = 0; i < 12; i++)
        {
            context.Add(
                new Reading
                {
                    SensorId = SensorA,
                    RecordedAt = Base.AddMinutes(i),
                    Value = i,
                    Status = i == 7 ? "alarm" : "ok"
                });
        }

        await context.SaveChangesAsync();
    }

    public ValueTask DisposeAsync()
        => ValueTask.CompletedTask;

    [Fact]
    public async Task Scalar_projection_reads_only_that_column()
    {
        await using var context = CreateContext();

        var query = context.Readings.Where(r => r.SensorId == SensorA).Select(r => r.Value);

        Assert.Contains("SELECT \"Value\" FROM", query.ToCqlString(), StringComparison.Ordinal);

        var values = await query.ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(12, values.Count);
        Assert.Contains(7d, values);
    }

    [Fact]
    public async Task Anonymous_projection_reads_only_the_columns_it_names()
    {
        await using var context = CreateContext();

        var query = context.Readings
            .Where(r => r.SensorId == SensorA)
            .Select(r => new { r.RecordedAt, r.Value });

        var cql = query.ToCqlString();
        Assert.Contains("SELECT \"RecordedAt\", \"Value\" FROM", cql, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Status\"", cql, StringComparison.Ordinal);

        var rows = await query.ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(12, rows.Count);
        Assert.Equal(Base, rows[0].RecordedAt);
    }

    /// <summary>
    ///     Cursor paging is what replaces <c>Skip</c>, which CQL cannot express.
    /// </summary>
    [Fact]
    public async Task Paging_walks_the_whole_result_set_without_overlap()
    {
        await using var context = CreateContext();
        var cancellationToken = TestContext.Current.CancellationToken;

        var seen = new List<DateTimeOffset>();
        string? token = null;
        var pages = 0;

        do
        {
            var page = await context.Readings
                .Where(r => r.SensorId == SensorA)
                .ToPageAsync(pageSize: 5, token, cancellationToken);

            seen.AddRange(page.Values.Select(r => r.RecordedAt));
            token = page.ContinuationToken;
            pages++;
        }
        while (token is not null && pages < 10);

        Assert.Equal(12, seen.Count);
        Assert.Equal(seen.Count, seen.Distinct().Count());
        Assert.True(pages >= 3);
    }

    [Fact]
    public async Task First_page_is_limited_to_the_requested_size()
    {
        await using var context = CreateContext();

        var page = await context.Readings
            .Where(r => r.SensorId == SensorA)
            .ToPageAsync(pageSize: 4, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(4, page.Values.Count);
        Assert.NotNull(page.ContinuationToken);
    }

    /// <summary>
    ///     The claim a plain <c>LIMIT</c> cannot make: a cap applied to every partition separately. With
    ///     <c>Take(6)</c> the server would stop after six rows total, most likely all from the first sensor;
    ///     this must return two from each of the three.
    /// </summary>
    [Fact]
    public async Task Per_partition_limit_caps_each_partition_separately()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var sensors = Enumerable.Range(0, 3).Select(_ => Guid.NewGuid()).ToList();

        await using (var seed = CreateContext())
        {
            foreach (var sensor in sensors)
            {
                for (var i = 0; i < 5; i++)
                {
                    seed.Add(
                        new Reading
                        {
                            SensorId = sensor,
                            RecordedAt = Base.AddMinutes(i),
                            Value = i,
                            Status = "ok"
                        });
                }
            }

            await seed.SaveChangesAsync(cancellationToken);
        }

        await using var context = CreateContext();

        var readings = await context.Readings
            .Where(r => sensors.Contains(r.SensorId))
            .PerPartitionLimit(2)
            .ToListAsync(cancellationToken);

        Assert.Equal(6, readings.Count);
        Assert.All(
            readings.GroupBy(r => r.SensorId),
            group => Assert.Equal(2, group.Count()));
    }

    /// <summary>
    ///     An indexed column can be restricted without opting into a scan.
    /// </summary>
    [Fact]
    public async Task Indexed_column_is_queryable_without_AllowFiltering()
    {
        await using var context = CreateContext();

        var alarms = await context.Readings
            .Where(r => r.Status == "alarm")
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Single(alarms);
        Assert.Equal(7d, alarms[0].Value);
    }
}
