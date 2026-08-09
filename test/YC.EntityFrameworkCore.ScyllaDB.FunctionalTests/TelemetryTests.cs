using System.Diagnostics;
using System.Diagnostics.Metrics;
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

    /// <summary>
    ///     A listener whose <c>ActivityStopped</c> callback throws, simulating a broken exporter or processor.
    /// </summary>
    private static IDisposable ListenWithThrowingStop()
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == ScyllaInstrumentation.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = _ => throw new InvalidOperationException(
                "Simulated exporter failure; the query this describes must be unaffected by it.")
        };

        ActivitySource.AddActivityListener(listener);

        return listener;
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
    ///     A write never sets <c>OperationName</c> explicitly — unlike a query, it always relies on the verb
    ///     being derived from the CQL — so it is the path where turning query text off could silently cost the
    ///     operation name and, with it, the span name. <c>WithQueryTextInTelemetry(false)</c> is only meant to
    ///     stop the CQL itself from being emitted, not to reduce span fidelity.
    /// </summary>
    [Fact]
    public async Task Write_span_keeps_its_operation_name_when_query_text_is_disabled()
    {
        var (captured, listener) = Listen();
        using (listener)
        {
            await using var context = CreateContext(queryText: false);

            context.Add(
                new Reading { SensorId = Guid.NewGuid(), RecordedAt = DateTimeOffset.UnixEpoch, Value = 1 });
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var span = Assert.Single(captured, a => (string?)a.GetTagItem("db.operation.name") == "INSERT");

        Assert.Equal($"INSERT {Keyspace}.readings", span.DisplayName);
        Assert.Equal("readings", span.GetTagItem("db.collection.name"));
        Assert.Null(span.GetTagItem("db.query.text"));
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

    /// <summary>
    ///     Pins the fix in <see cref="Storage.Internal.ScyllaClientWrapper" />'s <c>ExecuteAsync</c>: the
    ///     activity is stopped explicitly, inside the method's own <c>finally</c>, through
    ///     <see cref="ScyllaInstrumentation.StopActivity" />. It used to be stopped implicitly by a
    ///     <c>using var activity = ScyllaInstrumentation.StartActivity(...)</c> declaration instead — but a
    ///     <c>using var</c> declares its disposal in the <em>outermost</em> <c>finally</c> the compiler
    ///     generates for the method, which sits outside the <c>try</c>/<c>catch</c>/<c>finally</c> written in
    ///     the method body. Disposing an <see cref="Activity" /> is what runs a listener's
    ///     <c>ActivityStopped</c> callback, so under the old code a throwing listener would have turned this
    ///     otherwise-successful query into a faulted task. Reverting to <c>using var</c> makes this test fail.
    /// </summary>
    [Fact]
    public async Task Successful_query_is_not_faulted_by_a_listener_that_throws_on_stop()
    {
        using var throwingListener = ListenWithThrowingStop();

        await using var context = CreateContext();
        var sensorId = Guid.NewGuid();

        var readings = await context.Readings
            .Where(r => r.SensorId == sensorId)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Empty(readings);
    }

    /// <summary>
    ///     The other half of the pair above, and the more valuable one: on a genuinely failed statement, the
    ///     exception that reaches the caller must be the driver's own <see cref="Cassandra.DriverException" />,
    ///     not whatever a broken listener raised from <c>ActivityStopped</c>. Under the old
    ///     <c>using var activity = ScyllaInstrumentation.StartActivity(...)</c>, the implicit dispose ran in the
    ///     method's outermost <c>finally</c> — after the <c>catch</c> block had already recorded the real
    ///     exception — so a throwing listener there would have replaced the genuine
    ///     <see cref="Cassandra.DriverException" /> with its own <see cref="InvalidOperationException" /> before
    ///     it ever reached this test. Reverting <see cref="Storage.Internal.ScyllaClientWrapper" /> to
    ///     <c>using var</c> makes this test fail with the listener's exception instead.
    /// </summary>
    [Fact]
    public async Task Failed_statement_surfaces_its_own_exception_despite_a_listener_that_throws_on_stop()
    {
        using var throwingListener = ListenWithThrowingStop();

        await using var context = CreateContext();
        var client = context.GetService<Storage.Internal.IScyllaClientWrapper>();

        await Assert.ThrowsAnyAsync<Cassandra.DriverException>(
            () => client.ExecuteAsync(
                new Cassandra.SimpleStatement($"SELECT * FROM {Keyspace}.does_not_exist"),
                TestContext.Current.CancellationToken));
    }

    private static (List<(double Value, KeyValuePair<string, object?>[] Tags)> Measurements, MeterListener Listener)
        ListenToMeter()
    {
        var measurements = new List<(double, KeyValuePair<string, object?>[])>();

        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == ScyllaInstrumentation.Name)
                {
                    l.EnableMeasurementEvents(instrument);
                }
            }
        };

        listener.SetMeasurementEventCallback<double>(
            (_, value, tags, _) => measurements.Add((value, tags.ToArray())));

        listener.Start();

        return (measurements, listener);
    }

    /// <summary>
    ///     One histogram carries rate, errors and duration together, so this is the single instrument a
    ///     dashboard needs.
    /// </summary>
    [Fact]
    public async Task Every_statement_records_its_duration()
    {
        var (measurements, listener) = ListenToMeter();

        using (listener)
        {
            await using var context = CreateContext();

            var sensorId = Guid.NewGuid();

            await context.Readings
                .Where(r => r.SensorId == sensorId)
                .ToListAsync(TestContext.Current.CancellationToken);
        }

        var measurement = Assert.Single(
            measurements,
            m => m.Tags.Any(t => t.Key == "db.operation.name" && (string?)t.Value == "SELECT"));

        Assert.True(measurement.Value >= 0);

        var tags = measurement.Tags.ToDictionary(t => t.Key, t => t.Value, StringComparer.Ordinal);

        Assert.Equal("cassandra", tags["db.system.name"]);
        Assert.Equal(Keyspace, tags["db.namespace"]);
        Assert.Equal("readings", tags["db.collection.name"]);
        Assert.False(tags.ContainsKey("error.type"));
    }

    /// <summary>
    ///     The other half of the invariant pair: whilst <see cref="Every_statement_records_its_duration" />
    ///     proves the <c>error.type</c> tag is <em>absent</em> on success, this one proves it is <em>present</em>
    ///     on failure. Together they make the tag trustworthy enough to alert on: a tag that is always absent
    ///     and a tag that is always present are both indistinguishable from a broken one if one only ever tests
    ///     the one case.
    /// </summary>
    [Fact]
    public async Task A_failed_statement_is_tagged_with_the_error_type()
    {
        var (measurements, listener) = ListenToMeter();

        using (listener)
        {
            await using var context = CreateContext();
            var client = context.GetService<Storage.Internal.IScyllaClientWrapper>();

            await Assert.ThrowsAnyAsync<Cassandra.DriverException>(
                () => client.ExecuteAsync(
                    new Cassandra.SimpleStatement($"SELECT * FROM {Keyspace}.does_not_exist"),
                    TestContext.Current.CancellationToken));
        }

        Assert.Contains(measurements, m => m.Tags.Any(t => t.Key == "error.type"));
    }

    /// <summary>
    ///     The text carries no values — everything is bound through markers — but it does carry schema names,
    ///     which some environments treat as sensitive. Turning it off must leave the rest of the span intact.
    /// </summary>
    [Fact]
    public async Task Query_text_can_be_turned_off_without_losing_the_rest()
    {
        var (captured, listener) = Listen();
        using (listener)
        {
            await using var context = CreateContext(queryText: false);

            var sensorId = Guid.NewGuid();

            await context.Readings
                .Where(r => r.SensorId == sensorId)
                .ToListAsync(TestContext.Current.CancellationToken);
        }

        var span = Assert.Single(captured, a => (string?)a.GetTagItem("db.operation.name") == "SELECT");

        Assert.Null(span.GetTagItem("db.query.text"));
        Assert.Equal(Keyspace, span.GetTagItem("db.namespace"));
        Assert.Equal("readings", span.GetTagItem("db.collection.name"));
        Assert.NotNull(span.GetTagItem("cassandra.consistency.level"));
    }
}
