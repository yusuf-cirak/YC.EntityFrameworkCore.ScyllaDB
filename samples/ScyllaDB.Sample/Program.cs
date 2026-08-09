using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.EntityFrameworkCore;
using ScyllaDB.Sample;
using YC.EntityFrameworkCore.ScyllaDB;
using YC.EntityFrameworkCore.ScyllaDB.Diagnostics;

// Point at your own node with SCYLLA_HOST / SCYLLA_PORT, or run one with:
//   docker run --rm -p 9042:9042 scylladb/scylla:6.2 --smp 1 --developer-mode 1
var host = Environment.GetEnvironmentVariable("SCYLLA_HOST") ?? "127.0.0.1";
var port = int.TryParse(Environment.GetEnvironmentVariable("SCYLLA_PORT"), out var configured) ? configured : 9042;

Console.WriteLine($"Connecting to {host}:{port}");

using var telemetry = Telemetry.Start();

var options = new DbContextOptionsBuilder<MetricsContext>()
    .UseScyllaDb([host], keyspace: "sample_metrics", o => o.WithPort(port))
    .Options;

await using var context = new MetricsContext(options);

// ── Schema ───────────────────────────────────────────────────────────────────────────────────────────────
// Creates the keyspace, the tables and the materialized view, waiting for the cluster to agree on each change
// before the next one runs. Schema changes in ScyllaDB propagate eventually, not atomically.
await context.Database.EnsureCreatedAsync();
Console.WriteLine("Schema ready.");

// ── Writing ──────────────────────────────────────────────────────────────────────────────────────────────
var sensorId = Guid.NewGuid();
var start = DateTimeOffset.UtcNow.AddMinutes(-30);

for (var i = 0; i < 30; i++)
{
    context.Add(
        new Reading
        {
            SensorId = sensorId,
            RecordedAt = start.AddMinutes(i),
            Value = 20 + Math.Sin(i / 3.0) * 5,
            Status = i == 17 ? "alarm" : "ok"
        });
}

// All thirty rows share a partition, so they go out as one unlogged batch — atomic for that partition, and no
// more expensive than sending them separately. Rows in different partitions would go out independently, and
// that is not atomic; the provider logs a warning when it happens.
Console.WriteLine($"Wrote {await context.SaveChangesAsync()} readings.");

// ── Reading ──────────────────────────────────────────────────────────────────────────────────────────────
// The partition key is restricted, so this is a seek rather than a scan. Rows arrive in the declared
// clustering order without an ORDER BY, because that is the order they are stored in.
var cutoff = start.AddMinutes(20);

var recent = await context.Readings
    .Where(r => r.SensorId == sensorId && r.RecordedAt >= cutoff)
    .ToListAsync();

Console.WriteLine($"\n{recent.Count} readings since {cutoff:HH:mm}, newest first:");
foreach (var reading in recent.Take(3))
{
    Console.WriteLine($"  {reading.RecordedAt:HH:mm}  {reading.Value:F2}  {reading.Status}");
}

// Seeing what actually reached the server matters here: the difference between a partition seek and a
// cluster-wide scan is which restrictions were translated.
Console.WriteLine(
    "\nCQL:\n" + context.Readings.Where(r => r.SensorId == sensorId).Take(5).ToCqlString());

// ── Projection ───────────────────────────────────────────────────────────────────────────────────────────
// Only the named columns are fetched. CQL rows can be wide, and every column named is a cell read from storage.
var values = await context.Readings
    .Where(r => r.SensorId == sensorId)
    .Select(r => new { r.RecordedAt, r.Value })
    .ToListAsync();

Console.WriteLine($"\nProjected {values.Count} rows over two columns.");

// ── Paging ───────────────────────────────────────────────────────────────────────────────────────────────
// CQL has no OFFSET — rows are reached by seeking, not by counting past the ones before them — so paging is
// cursor-based. The token is the server's paging state; it is not signed, so never hand it to an untrusted
// client without signing it first.
//
// Loop until the token is null, never until a page comes back short. Thirty rows at ten per page take four
// round trips here, not three: the coordinator hands back a token after the third page because it cannot yet
// prove there is nothing left, and the fourth page returns empty. A short page does not mean the end.
var pages = 0;
var total = 0;
string? continuationToken = null;

do
{
    var page = await context.Readings
        .Where(r => r.SensorId == sensorId)
        .ToPageAsync(pageSize: 10, continuationToken);

    pages++;
    total += page.Values.Count;
    continuationToken = page.ContinuationToken;
}
while (continuationToken is not null);

Console.WriteLine($"Paged through {total} rows in {pages} pages.");

// ── Materialized view ────────────────────────────────────────────────────────────────────────────────────
// Answers a question the base table cannot: "which readings are in alarm?" The view is maintained by the
// server and is read-only — writing to it throws.
var alarms = await context.ReadingsByStatus
    .Where(r => r.Status == "alarm")
    .ToListAsync();

Console.WriteLine($"\n{alarms.Count} reading(s) in alarm, found through the materialized view.");

// ── Time to live ─────────────────────────────────────────────────────────────────────────────────────────
var sessionId = Guid.NewGuid();

using (context.UseScyllaWriteOptions(new ScyllaWriteOptions(TimeToLive: TimeSpan.FromSeconds(5))))
{
    context.Add(new Session { Id = sessionId, User = "ada", Version = 1 });
    await context.SaveChangesAsync();
}

Console.WriteLine("\nWrote a session that expires in five seconds, created conditionally so Paxos owns the partition.");

// ── Lightweight transaction ──────────────────────────────────────────────────────────────────────────────
// The concurrency token turns this update into a compare-and-set. A second writer working from the same
// original version loses, which an ordinary CQL write could never detect — it reports no affected-row count.
//
// The row was created with UseInsertIfNotExists, and that is load-bearing rather than decorative. Paxos only
// orders lightweight transactions against each other; a partition also written by ordinary statements is
// outside that guarantee, and a later IF can match a value the row has already moved past. Mixing the two on
// one partition makes the concurrency check quietly unsound, which this sample demonstrated the hard way.
await using (var winner = new MetricsContext(options))
{
    var winning = await winner.Sessions.FirstAsync(s => s.Id == sessionId);
    winning.User = "grace";
    winning.Version = 2;
    await winner.SaveChangesAsync();
}

Console.WriteLine("Updated the session through a lightweight transaction.");

await using (var stale = new MetricsContext(options))
{
    var losing = new Session { Id = sessionId, User = "ada", Version = 1 };
    stale.Attach(losing);
    losing.User = "hopper";
    losing.Version = 2;

    try
    {
        await stale.SaveChangesAsync();
        Console.WriteLine("  (expected a conflict and did not get one)");
    }
    catch (DbUpdateConcurrencyException exception)
    {
        Console.WriteLine($"  A stale writer was rejected: {exception.Message}");
    }
}

// ── What CQL will not do ─────────────────────────────────────────────────────────────────────────────────
// Each of these fails while the query is being translated, not when it runs, and says what to do instead.
Console.WriteLine("\nQueries CQL cannot answer, refused at translation time:");

Report(() => context.Readings.Skip(10).ToCqlString());
Report(() => context.Readings.Where(r => r.Status == "alarm").ToCqlString());
Report(() => context.Readings.Where(r => r.SensorId == sensorId).OrderBy(r => r.Value).ToCqlString());

await context.Database.EnsureDeletedAsync();
Console.WriteLine("\nDropped the keyspace. Done.");

static void Report(Func<string> query)
{
    try
    {
        query();
        Console.WriteLine("  (expected a refusal and did not get one)");
    }
    catch (InvalidOperationException exception)
    {
        Console.WriteLine($"  • {exception.Message}");
    }
}

/// <summary>
///     Listens to the provider's own telemetry and prints it, standing in for an OpenTelemetry exporter.
/// </summary>
/// <remarks>
///     A real application calls <c>AddSource</c> and <c>AddMeter</c> with the same name and never writes code
///     like this. It is here to make the spans and measurements visible without pulling an exporter into a
///     sample.
/// </remarks>
file static class Telemetry
{
    public static IDisposable Start()
    {
        var activityListener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == ScyllaInstrumentation.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => Console.WriteLine(
                $"    [trace] {activity.DisplayName} {activity.Duration.TotalMilliseconds:F1}ms"
                + $"\n            {activity.GetTagItem("db.query.text")}")
        };

        ActivitySource.AddActivityListener(activityListener);

        var meterListener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == ScyllaInstrumentation.Name)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            }
        };

        meterListener.Start();

        return new Composite(activityListener, meterListener);
    }

    private sealed class Composite(IDisposable first, IDisposable second) : IDisposable
    {
        public void Dispose()
        {
            first.Dispose();
            second.Dispose();
        }
    }
}
