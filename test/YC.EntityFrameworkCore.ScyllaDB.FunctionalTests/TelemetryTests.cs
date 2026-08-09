using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using YC.EntityFrameworkCore.ScyllaDB.Diagnostics;

namespace YC.EntityFrameworkCore.ScyllaDB.FunctionalTests;

/// <summary>
///     Telemetry against a real node.
/// </summary>
/// <remarks>
///     The instrumentation lives inside the client wrapper, so a unit test with a fake wrapper would verify
///     nothing real. These listen to the actual source while driving the provider end to end.
/// </remarks>
[Collection(ScyllaCollection.Name)]
public class TelemetryTests(ScyllaContainerFixture fixture) : IAsyncLifetime
{
    private const string Keyspace = "telemetry_tests";

    private sealed class Reading
    {
        public Guid SensorId { get; set; }

        public DateTimeOffset RecordedAt { get; set; }

        public double Value { get; set; }
    }

    private sealed class TelemetryContext(DbContextOptions options) : DbContext(options)
    {
        public DbSet<Reading> Readings
            => Set<Reading>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.Entity<Reading>(e =>
            {
                e.ToScyllaTable("readings");
                e.HasPartitionKey(r => r.SensorId);
                e.HasClusteringKey(r => r.RecordedAt);
            });
    }

    private TelemetryContext CreateContext(bool queryText = true)
        => new(
            new DbContextOptionsBuilder<TelemetryContext>()
                .UseScyllaDb(
                    fixture.ContactPoints, Keyspace, o =>
                    {
                        o.WithPort(fixture.Port);
                        o.WithQueryTextInTelemetry(queryText);
                    })
                .Options);

    private static (List<Activity> Captured, IDisposable Listener) Listen()
    {
        var captured = new List<Activity>();

        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == ScyllaInstrumentation.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = captured.Add
        };

        ActivitySource.AddActivityListener(listener);

        return (captured, listener);
    }

    public async ValueTask InitializeAsync()
    {
        await using var context = CreateContext();
        await context.Database.EnsureCreatedAsync();
    }

    public ValueTask DisposeAsync()
        => ValueTask.CompletedTask;

    [Fact]
    public async Task Query_emits_a_client_span_with_the_semantic_convention_attributes()
    {
        var (captured, listener) = Listen();
        using (listener)
        {
            await using var context = CreateContext();

            // Captured rather than inlined: EF Core treats Guid.NewGuid() as a server-evaluable call (like
            // SQL Server's NEWID()) rather than a client-side constant, and this provider does not translate
            // it. A captured value sidesteps that — the span this test checks does not depend on any rows
            // matching.
            var sensorId = Guid.NewGuid();

            await context.Readings
                .Where(r => r.SensorId == sensorId)
                .ToListAsync(TestContext.Current.CancellationToken);
        }

        var span = Assert.Single(captured, a => (string?)a.GetTagItem("db.operation.name") == "SELECT");

        Assert.Equal(ActivityKind.Client, span.Kind);
        Assert.Equal($"SELECT {Keyspace}.readings", span.DisplayName);
        Assert.Equal("cassandra", span.GetTagItem("db.system.name"));
        Assert.Equal(Keyspace, span.GetTagItem("db.namespace"));
        Assert.Equal("readings", span.GetTagItem("db.collection.name"));
        Assert.Contains("SELECT", (string)span.GetTagItem("db.query.text")!, StringComparison.Ordinal);
        Assert.NotNull(span.GetTagItem("cassandra.consistency.level"));
        Assert.NotNull(span.GetTagItem("db.response.returned_rows"));
    }

    /// <summary>
    ///     CQL is a paged protocol, so "rows returned" means the rows in this response — read without
    ///     advancing the enumerator, since draining the result set to count it would defeat the point.
    /// </summary>
    [Fact]
    public async Task Returned_row_count_reflects_the_rows_in_the_response()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var sensorId = Guid.NewGuid();

        await using (var seed = CreateContext())
        {
            for (var i = 0; i < 3; i++)
            {
                seed.Add(
                    new Reading
                    {
                        SensorId = sensorId,
                        RecordedAt = DateTimeOffset.UnixEpoch.AddMinutes(i),
                        Value = i
                    });
            }

            await seed.SaveChangesAsync(cancellationToken);
        }

        var (captured, listener) = Listen();
        using (listener)
        {
            await using var context = CreateContext();

            var readings = await context.Readings
                .Where(r => r.SensorId == sensorId)
                .ToListAsync(cancellationToken);

            Assert.Equal(3, readings.Count);
        }

        var span = Assert.Single(captured, a => (string?)a.GetTagItem("db.operation.name") == "SELECT");

        Assert.Equal(3, span.GetTagItem("db.response.returned_rows"));
    }

    [Fact]
    public async Task Insert_emits_its_own_span()
    {
        var (captured, listener) = Listen();
        using (listener)
        {
            await using var context = CreateContext();

            context.Add(
                new Reading { SensorId = Guid.NewGuid(), RecordedAt = DateTimeOffset.UnixEpoch, Value = 1 });
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var span = Assert.Single(captured, a => (string?)a.GetTagItem("db.operation.name") == "INSERT");

        Assert.Equal($"INSERT {Keyspace}.readings", span.DisplayName);
        Assert.Equal("readings", span.GetTagItem("db.collection.name"));
    }

    /// <summary>
    ///     Rows sharing a partition go out as one batch, and the batch size is what tells a reader whether
    ///     the write was one round trip or many.
    /// </summary>
    [Fact]
    public async Task Batched_write_reports_its_size()
    {
        var (captured, listener) = Listen();
        using (listener)
        {
            await using var context = CreateContext();
            var sensorId = Guid.NewGuid();

            for (var i = 0; i < 4; i++)
            {
                context.Add(
                    new Reading
                    {
                        SensorId = sensorId,
                        RecordedAt = DateTimeOffset.UnixEpoch.AddMinutes(i),
                        Value = i
                    });
            }

            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var span = Assert.Single(captured, a => a.GetTagItem("db.operation.batch.size") is not null);

        Assert.Equal(4, span.GetTagItem("db.operation.batch.size"));
    }

    /// <summary>
    ///     Telemetry must record the failure and leave the exception alone.
    /// </summary>
    [Fact]
    public async Task Failed_statement_records_the_error_type_and_rethrows()
    {
        var (captured, listener) = Listen();

        using (listener)
        {
            await using var context = CreateContext();
            var client = context.GetService<Storage.Internal.IScyllaClientWrapper>();

            await Assert.ThrowsAnyAsync<Cassandra.DriverException>(
                () => client.ExecuteAsync(
                    new Cassandra.SimpleStatement($"SELECT * FROM {Keyspace}.does_not_exist"),
                    TestContext.Current.CancellationToken));
        }

        var span = Assert.Single(captured, a => a.GetTagItem("error.type") is not null);

        Assert.Equal(ActivityStatusCode.Error, span.Status);
    }
}
