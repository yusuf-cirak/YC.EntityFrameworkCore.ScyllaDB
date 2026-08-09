# Observability Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Emit an OpenTelemetry-compatible span and a duration histogram for every CQL statement the provider sends.

**Architecture:** A static `ScyllaInstrumentation` owns an `ActivitySource` and a `Meter`, both named `YC.EntityFrameworkCore.ScyllaDB`. Every statement already funnels through `ScyllaClientWrapper.ExecuteAsync`, so instrumentation goes there alone. Call sites pass a `ScyllaOperationContext` carrying what only they know — operation name, keyspace, table, CQL text.

**Tech Stack:** .NET 10, EF Core 10, `System.Diagnostics.DiagnosticSource` (already a transitive dependency — no new package), xUnit v3, Testcontainers.

## Global Constraints

- No new package references. `ActivitySource` and `Meter` come from `System.Diagnostics.DiagnosticSource`, which ships with `Microsoft.EntityFrameworkCore`.
- Source and meter name: `YC.EntityFrameworkCore.ScyllaDB`. Version: the assembly version.
- `db.system.name` is always `cassandra`. The semantic conventions define no ScyllaDB value.
- Parameter values are never emitted. `db.query.parameter.<key>` is out of scope entirely.
- Telemetry must never throw, never swallow, and never alter an exception.
- Warnings are errors repo-wide. File-scoped namespaces, `public virtual` on non-static public members, `StringComparison.Ordinal` on every comparison.
- Every public member needs an XML doc comment explaining *why*, matching the surrounding style.

---

### Task 1: Instrumentation primitives and the query-text option

**Files:**
- Create: `src/YC.EntityFrameworkCore.ScyllaDB/Diagnostics/ScyllaInstrumentation.cs`
- Modify: `src/YC.EntityFrameworkCore.ScyllaDB/Infrastructure/Internal/ScyllaOptionsExtension.cs`
- Modify: `src/YC.EntityFrameworkCore.ScyllaDB/Infrastructure/Internal/ScyllaSingletonOptions.cs`
- Modify: `src/YC.EntityFrameworkCore.ScyllaDB/Infrastructure/ScyllaDbContextOptionsBuilder.cs`
- Test: `test/YC.EntityFrameworkCore.ScyllaDB.UnitTests/Diagnostics/InstrumentationTests.cs`

**Interfaces:**
- Produces: `ScyllaInstrumentation.Name` (const string), `ScyllaInstrumentation.StartActivity(in ScyllaOperationContext, IStatement?)` returning `Activity?`, `ScyllaInstrumentation.RecordDuration(TimeSpan, in ScyllaOperationContext, string? errorType)`, and the `readonly record struct ScyllaOperationContext(string? OperationName, string? Keyspace, string? Table, string? QueryText)`.
- Produces: `IScyllaSingletonOptions.EmitQueryTextInTelemetry` (bool) and `ScyllaDbContextOptionsBuilder.WithQueryTextInTelemetry(bool)`.

- [ ] **Step 1: Write the failing test**

Create `test/YC.EntityFrameworkCore.ScyllaDB.UnitTests/Diagnostics/InstrumentationTests.cs`:

```csharp
using System.Diagnostics;
using YC.EntityFrameworkCore.ScyllaDB.Diagnostics;

namespace YC.EntityFrameworkCore.ScyllaDB.UnitTests.Diagnostics;

/// <summary>
///     The source name is a public contract: consumers write it into their OpenTelemetry configuration, so
///     changing it silently turns their telemetry off.
/// </summary>
public class InstrumentationTests
{
    private static (List<Activity> Captured, ActivityListener Listener) Listen()
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

    [Fact]
    public void Source_is_named_for_the_package()
    {
        Assert.Equal("YC.EntityFrameworkCore.ScyllaDB", ScyllaInstrumentation.Name);
    }

    [Fact]
    public void Activity_carries_the_database_semantic_convention_attributes()
    {
        var (captured, listener) = Listen();
        using (listener)
        {
            using (var activity = ScyllaInstrumentation.StartActivity(
                       new ScyllaOperationContext("SELECT", "metrics", "readings", "SELECT * FROM x"),
                       statement: null))
            {
                Assert.NotNull(activity);
            }
        }

        var recorded = Assert.Single(captured);

        Assert.Equal("SELECT metrics.readings", recorded.DisplayName);
        Assert.Equal(ActivityKind.Client, recorded.Kind);
        Assert.Equal("cassandra", recorded.GetTagItem("db.system.name"));
        Assert.Equal("metrics", recorded.GetTagItem("db.namespace"));
        Assert.Equal("readings", recorded.GetTagItem("db.collection.name"));
        Assert.Equal("SELECT", recorded.GetTagItem("db.operation.name"));
    }

    /// <summary>
    ///     Span names must stay low-cardinality, so a missing table or keyspace drops out of the name rather
    ///     than appearing as a placeholder.
    /// </summary>
    [Fact]
    public void Activity_name_drops_the_parts_it_does_not_have()
    {
        var (captured, listener) = Listen();
        using (listener)
        {
            ScyllaInstrumentation
                .StartActivity(new ScyllaOperationContext("CREATE KEYSPACE", "metrics", null, null), null)
                ?.Dispose();

            ScyllaInstrumentation
                .StartActivity(new ScyllaOperationContext("SELECT", null, null, null), null)
                ?.Dispose();
        }

        Assert.Equal("CREATE KEYSPACE metrics", captured[0].DisplayName);
        Assert.Equal("SELECT", captured[1].DisplayName);
    }

    /// <summary>
    ///     The operation name is what makes a span readable in a trace view, so it is derived from the CQL when
    ///     the call site did not supply one.
    /// </summary>
    [Theory]
    [InlineData("SELECT \"a\" FROM \"k\".\"t\"", "SELECT")]
    [InlineData("INSERT INTO \"k\".\"t\" (\"a\") VALUES (?)", "INSERT")]
    [InlineData("CREATE TABLE IF NOT EXISTS \"k\".\"t\" (a int)", "CREATE TABLE")]
    [InlineData("DROP KEYSPACE IF EXISTS \"k\"", "DROP KEYSPACE")]
    public void Operation_name_is_derived_from_the_statement_when_not_supplied(string cql, string expected)
    {
        var (captured, listener) = Listen();
        using (listener)
        {
            ScyllaInstrumentation
                .StartActivity(new ScyllaOperationContext(null, "k", null, cql), null)
                ?.Dispose();
        }

        Assert.Equal(expected, Assert.Single(captured).GetTagItem("db.operation.name"));
    }

    /// <summary>
    ///     Nothing listening means no activity is created at all, which is what makes always-on instrumentation
    ///     free.
    /// </summary>
    [Fact]
    public void No_activity_is_created_when_nothing_is_listening()
    {
        Assert.Null(
            ScyllaInstrumentation.StartActivity(
                new ScyllaOperationContext("SELECT", "metrics", "readings", null), statement: null));
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test test/YC.EntityFrameworkCore.ScyllaDB.UnitTests --filter "FullyQualifiedName~InstrumentationTests"`
Expected: build failure — `ScyllaInstrumentation` and `ScyllaOperationContext` do not exist.

- [ ] **Step 3: Create the instrumentation primitives**

Create `src/YC.EntityFrameworkCore.ScyllaDB/Diagnostics/ScyllaInstrumentation.cs`:

```csharp
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Cassandra;

namespace YC.EntityFrameworkCore.ScyllaDB.Diagnostics;

/// <summary>
///     What a call site knows about the statement it is about to send.
/// </summary>
/// <param name="OperationName">
///     The CQL verb, for example <c>SELECT</c> or <c>CREATE TABLE</c>. Derived from
///     <paramref name="QueryText" /> when null.
/// </param>
/// <param name="Keyspace">The keyspace the statement targets.</param>
/// <param name="Table">The table, for statements that touch exactly one.</param>
/// <param name="QueryText">The CQL, with bind markers rather than values.</param>
/// <remarks>
///     The client wrapper receives an <see cref="IStatement" />, which does not carry the keyspace, table or
///     operation in a form worth parsing back out. The call sites do know them, so they pass them along rather
///     than the wrapper guessing.
/// </remarks>
public readonly record struct ScyllaOperationContext(
    string? OperationName = null,
    string? Keyspace = null,
    string? Table = null,
    string? QueryText = null);

/// <summary>
///     The provider's <see cref="ActivitySource" /> and <see cref="Meter" />.
/// </summary>
/// <remarks>
///     <para>
///         Both are named <see cref="Name" />, which is the string a consumer passes to
///         <c>AddSource</c> and <c>AddMeter</c>. That makes OpenTelemetry a listener rather than a dependency —
///         nothing here references it — and lets <c>dotnet-trace</c> and <c>dotnet-counters</c> work unchanged.
///     </para>
///     <para>
///         When nothing is listening, <see cref="ActivitySource.StartActivity(string, ActivityKind)" /> returns
///         null and no attribute is ever computed, so instrumentation is always on and costs nothing unobserved.
///     </para>
/// </remarks>
public static class ScyllaInstrumentation
{
    /// <summary>
    ///     The name of both the activity source and the meter. Part of the public contract: a consumer writes
    ///     this into their telemetry configuration.
    /// </summary>
    public const string Name = "YC.EntityFrameworkCore.ScyllaDB";

    private static readonly string Version =
        typeof(ScyllaInstrumentation).Assembly.GetName().Version?.ToString() ?? "0.0.0";

    private static readonly ActivitySource Source = new(Name, Version);

    private static readonly Meter Meter = new(Name, Version);

    /// <summary>
    ///     Duration of database client operations, as defined by the database semantic conventions.
    /// </summary>
    /// <remarks>
    ///     One histogram answers rate, errors and duration together. Separate counters would be derivable from
    ///     it and would only add cardinality.
    /// </remarks>
    private static readonly Histogram<double> OperationDuration = Meter.CreateHistogram<double>(
        "db.client.operation.duration",
        unit: "s",
        description: "Duration of database client operations.");

    /// <summary>
    ///     Starts a client span for a statement, or returns null when nothing is listening.
    /// </summary>
    public static Activity? StartActivity(in ScyllaOperationContext context, IStatement? statement)
    {
        var operation = context.OperationName ?? DeriveOperationName(context.QueryText);
        var activity = Source.StartActivity(SpanName(operation, context), ActivityKind.Client);

        if (activity is null)
        {
            return null;
        }

        activity.SetTag("db.system.name", "cassandra");

        if (operation is not null)
        {
            activity.SetTag("db.operation.name", operation);
        }

        if (context.Keyspace is { Length: > 0 })
        {
            activity.SetTag("db.namespace", context.Keyspace);
        }

        if (context.Table is { Length: > 0 })
        {
            activity.SetTag("db.collection.name", context.Table);
        }

        if (statement is not null)
        {
            if (statement.PageSize > 0)
            {
                activity.SetTag("cassandra.page.size", statement.PageSize);
            }

            if (statement.IsIdempotent is { } idempotent)
            {
                activity.SetTag("cassandra.query.idempotent", idempotent);
            }

            if (statement is BatchStatement batch)
            {
                activity.SetTag("db.operation.batch.size", batch.Statements.Count());
            }
        }

        return activity;
    }

    /// <summary>
    ///     Records what the response tells us: the consistency actually achieved, and how many rows came back.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The achieved consistency is not necessarily the one configured — the driver applies its own
    ///         default when none was set, and that default is what a reader needs to see.
    ///     </para>
    ///     <para>
    ///         <see cref="RowSet.GetAvailableWithoutFetching" /> counts the rows already buffered from this
    ///         response without advancing the enumerator or fetching another page. CQL is a paged protocol, so
    ///         "rows returned by the operation" is the page; counting the whole result set would mean draining
    ///         it, which is neither free nor what the attribute means.
    ///     </para>
    /// </remarks>
    public static void RecordResult(Activity? activity, RowSet rowSet)
    {
        if (activity is null)
        {
            return;
        }

        activity.SetTag(
            "cassandra.consistency.level",
            rowSet.Info.AchievedConsistency.ToString().ToLowerInvariant());

        activity.SetTag("db.response.returned_rows", rowSet.GetAvailableWithoutFetching());
    }

    /// <summary>
    ///     Marks the span as failed and records the exception type.
    /// </summary>
    /// <remarks>
    ///     The driver exposes no protocol error code on a common base type, so <c>db.response.status_code</c>
    ///     is not emitted; the exception type is what identifies the failure consistently.
    /// </remarks>
    public static void RecordException(Activity? activity, Exception exception)
    {
        activity?.SetTag("error.type", exception.GetType().FullName);
        activity?.SetStatus(ActivityStatusCode.Error, exception.Message);
    }

    /// <summary>
    ///     Records how long an operation took, whether it succeeded or failed.
    /// </summary>
    public static void RecordDuration(TimeSpan elapsed, in ScyllaOperationContext context, string? errorType)
    {
        if (!OperationDuration.Enabled)
        {
            return;
        }

        var tags = new TagList { { "db.system.name", "cassandra" } };

        if (context.OperationName ?? DeriveOperationName(context.QueryText) is { } operation)
        {
            tags.Add("db.operation.name", operation);
        }

        if (context.Keyspace is { Length: > 0 })
        {
            tags.Add("db.namespace", context.Keyspace);
        }

        if (context.Table is { Length: > 0 })
        {
            tags.Add("db.collection.name", context.Table);
        }

        if (errorType is not null)
        {
            tags.Add("error.type", errorType);
        }

        OperationDuration.Record(elapsed.TotalSeconds, tags);
    }

    /// <summary>
    ///     Builds a low-cardinality span name, dropping the parts that are unknown.
    /// </summary>
    private static string SpanName(string? operation, in ScyllaOperationContext context)
        => (operation, context.Keyspace, context.Table) switch
        {
            (null, _, _) => "CQL",
            ({ } op, { Length: > 0 } keyspace, { Length: > 0 } table) => $"{op} {keyspace}.{table}",
            ({ } op, { Length: > 0 } keyspace, _) => $"{op} {keyspace}",
            ({ } op, _, _) => op
        };

    /// <summary>
    ///     Reads the CQL verb off the front of a statement.
    /// </summary>
    /// <remarks>
    ///     DDL verbs are two words — <c>CREATE TABLE</c>, <c>DROP KEYSPACE</c> — and the distinction is worth
    ///     keeping, so those take a second token.
    /// </remarks>
    private static string? DeriveOperationName(string? cql)
    {
        if (cql is null)
        {
            return null;
        }

        var tokens = cql.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);

        if (tokens.Length == 0)
        {
            return null;
        }

        var first = tokens[0].ToUpperInvariant();

        return first is "CREATE" or "DROP" or "ALTER" && tokens.Length > 1
            ? $"{first} {tokens[1].ToUpperInvariant()}"
            : first;
    }
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test test/YC.EntityFrameworkCore.ScyllaDB.UnitTests --filter "FullyQualifiedName~InstrumentationTests"`
Expected: PASS, 7 tests.

- [ ] **Step 5: Add the query-text option**

In `ScyllaOptionsExtension.cs`, add the backing property beside the other options:

```csharp
    /// <summary>
    ///     Whether the CQL text is attached to telemetry spans as <c>db.query.text</c>.
    /// </summary>
    /// <remarks>
    ///     On by default. Every value the provider sends goes through a bind marker, so the text carries no
    ///     literals — what it does expose is keyspace, table and column names, which is the single most useful
    ///     field for explaining why a query is slow.
    /// </remarks>
    public virtual bool EmitQueryTextInTelemetry { get; private set; } = true;
```

Copy it in the copy-constructor beside `EnableTracing`:

```csharp
        EmitQueryTextInTelemetry = copyFrom.EmitQueryTextInTelemetry;
```

Add the `With` method beside `WithTracing`:

```csharp
    public virtual ScyllaOptionsExtension WithQueryTextInTelemetry(bool emitQueryText)
    {
        var clone = Clone();
        clone.EmitQueryTextInTelemetry = emitQueryText;
        return clone;
    }
```

And fold it into `ExtensionInfo.GetServiceProviderHashCode`, beside `hash.Add(Extension.EnableTracing);`:

```csharp
            hash.Add(Extension.EmitQueryTextInTelemetry);
```

- [ ] **Step 6: Surface the option on the singleton options and the builder**

In `ScyllaSingletonOptions.cs`, add to the interface beside `EnableTracing`:

```csharp
    bool EmitQueryTextInTelemetry { get; }
```

Add the implementation property:

```csharp
    public virtual bool EmitQueryTextInTelemetry { get; private set; } = true;
```

And in `Initialize`, beside `EnableTracing = extension.EnableTracing;`:

```csharp
        EmitQueryTextInTelemetry = extension.EmitQueryTextInTelemetry;
```

In `ScyllaDbContextOptionsBuilder.cs`, add beside `WithTracing`:

```csharp
    /// <summary>
    ///     Whether telemetry spans carry the CQL text. On by default.
    /// </summary>
    /// <remarks>
    ///     Turn it off where schema names are themselves sensitive. Values never appear either way — the
    ///     provider binds everything through markers.
    /// </remarks>
    public virtual ScyllaDbContextOptionsBuilder WithQueryTextInTelemetry(bool emitQueryText = true)
        => WithOption(e => e.WithQueryTextInTelemetry(emitQueryText));
```

- [ ] **Step 7: Run the whole unit suite**

Run: `dotnet build YC.EntityFrameworkCore.ScyllaDB.slnx && dotnet test test/YC.EntityFrameworkCore.ScyllaDB.UnitTests`
Expected: build clean with zero warnings; all tests pass.

- [ ] **Step 8: Commit**

```bash
git add src/YC.EntityFrameworkCore.ScyllaDB/Diagnostics/ScyllaInstrumentation.cs \
        src/YC.EntityFrameworkCore.ScyllaDB/Infrastructure \
        test/YC.EntityFrameworkCore.ScyllaDB.UnitTests/Diagnostics/InstrumentationTests.cs
git commit -m "feat: add the telemetry activity source and meter"
```

---

### Task 2: Emit a span for every statement

**Files:**
- Modify: `src/YC.EntityFrameworkCore.ScyllaDB/Storage/Internal/ScyllaClientWrapper.cs`
- Modify: `src/YC.EntityFrameworkCore.ScyllaDB/Query/Internal/ScyllaQueryingEnumerable.cs`
- Modify: `src/YC.EntityFrameworkCore.ScyllaDB/Storage/Internal/ScyllaDatabaseWrapper.cs`
- Modify: `src/YC.EntityFrameworkCore.ScyllaDB/Storage/Internal/ScyllaDatabaseCreator.cs`
- Test: `test/YC.EntityFrameworkCore.ScyllaDB.FunctionalTests/TelemetryTests.cs`

**Interfaces:**
- Consumes: `ScyllaInstrumentation.StartActivity`, `RecordResult`, `RecordException`, `RecordDuration`, `ScyllaOperationContext` from Task 1.
- Produces: `IScyllaClientWrapper.ExecuteAsync(IStatement, ScyllaOperationContext, CancellationToken)`. The two-argument overload `ExecuteAsync(IStatement, CancellationToken)` stays, passing `default` for the context.

- [ ] **Step 1: Write the failing test**

Create `test/YC.EntityFrameworkCore.ScyllaDB.FunctionalTests/TelemetryTests.cs`:

```csharp
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
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

            await context.Readings
                .Where(r => r.SensorId == Guid.NewGuid())
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
```

Add the missing using for `GetService` at the top of the file:

```csharp
using Microsoft.EntityFrameworkCore.Infrastructure;
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test test/YC.EntityFrameworkCore.ScyllaDB.FunctionalTests --filter "FullyQualifiedName~TelemetryTests"`
Expected: FAIL — no spans are captured, so `Assert.Single` throws.

- [ ] **Step 3: Instrument the client wrapper**

In `ScyllaClientWrapper.cs`, add the usings:

```csharp
using System.Diagnostics;
using YC.EntityFrameworkCore.ScyllaDB.Diagnostics;
```

Add the context-carrying overload to `IScyllaClientWrapper`, beside the existing `ExecuteAsync(IStatement, CancellationToken)`:

```csharp
    /// <summary>
    ///     Executes a statement, attaching what the caller knows about it to the telemetry span.
    /// </summary>
    Task<RowSet> ExecuteAsync(
        IStatement statement,
        ScyllaOperationContext context,
        CancellationToken cancellationToken = default);
```

Replace the body of the existing `ExecuteAsync(IStatement, CancellationToken)` so it delegates:

```csharp
    public virtual Task<RowSet> ExecuteAsync(
        IStatement statement,
        CancellationToken cancellationToken = default)
        => ExecuteAsync(statement, default, cancellationToken);

    public virtual async Task<RowSet> ExecuteAsync(
        IStatement statement,
        ScyllaOperationContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(statement);

        var prepared = ApplyDefaults(statement);

        if (!_options.EmitQueryTextInTelemetry)
        {
            context = context with { QueryText = null };
        }

        using var activity = ScyllaInstrumentation.StartActivity(context, prepared);

        if (activity is not null && context.QueryText is { } queryText)
        {
            activity.SetTag("db.query.text", queryText);
        }

        var startedAt = Stopwatch.GetTimestamp();
        string? errorType = null;

        try
        {
            var session = await GetSessionAsync(cancellationToken).ConfigureAwait(false);

            // The execution profile is not a property of the statement; it is chosen per execution.
            var rowSet = _options.ExecutionProfile is { } executionProfile
                ? await session.ExecuteAsync(prepared, executionProfile).ConfigureAwait(false)
                : await session.ExecuteAsync(prepared).ConfigureAwait(false);

            ScyllaInstrumentation.RecordResult(activity, rowSet);

            return rowSet;
        }
        catch (Exception exception)
        {
            // Recorded, never swallowed: a failure to observe must not become a failure to query.
            errorType = exception.GetType().FullName;
            ScyllaInstrumentation.RecordException(activity, exception);
            throw;
        }
        finally
        {
            ScyllaInstrumentation.RecordDuration(Stopwatch.GetElapsedTime(startedAt), context, errorType);
        }
    }
```

Note: the `QueryText`-stripping happens before `StartActivity` so the option also keeps the text out of any derived operation name path.

- [ ] **Step 4: Pass the context from the query path**

In `ScyllaQueryingEnumerable.cs`, inside `GetAsyncEnumerator`, replace the execute call:

```csharp
        var statement = await clientWrapper.BindAsync(cql, values, cancellationToken).ConfigureAwait(false);

        var rowSet = await clientWrapper.ExecuteAsync(
                statement,
                new ScyllaOperationContext(
                    "SELECT",
                    _select.EntityType.GetKeyspace(),
                    _select.EntityType.GetScyllaTableName(),
                    cql),
                cancellationToken)
            .ConfigureAwait(false);
```

Do the same inside `ToPageAsync`, replacing its execute call:

```csharp
        var rowSet = await clientWrapper.ExecuteAsync(
                statement,
                new ScyllaOperationContext(
                    "SELECT",
                    _select.EntityType.GetKeyspace(),
                    _select.EntityType.GetScyllaTableName(),
                    cql),
                cancellationToken)
            .ConfigureAwait(false);
```

Add the using:

```csharp
using YC.EntityFrameworkCore.ScyllaDB.Diagnostics;
```

- [ ] **Step 5: Pass the context from the write path**

In `ScyllaDatabaseWrapper.cs`, add the using:

```csharp
using YC.EntityFrameworkCore.ScyllaDB.Diagnostics;
```

Add a helper beside the other private methods:

```csharp
    /// <summary>
    ///     Describes a write for telemetry. A batch reports the table it targets but no single operation name,
    ///     since it is the batch itself that was executed.
    /// </summary>
    private static ScyllaOperationContext ContextFor(ScyllaWriteOperation operation)
        => new(
            null,
            operation.PartitionKey.Keyspace,
            operation.PartitionKey.Table,
            operation.Cql);
```

In `ExecuteSingleAsync`, pass it:

```csharp
        await _clientWrapper.ExecuteAsync(statement, ContextFor(operation), cancellationToken)
            .ConfigureAwait(false);
```

In `ExecuteConditionalAsync`, pass it:

```csharp
        var rowSet = await _clientWrapper.ExecuteAsync(statement, ContextFor(operation), cancellationToken)
            .ConfigureAwait(false);
```

In `ExecuteBatchAsync`, pass a context describing the batch — the CQL text of a batch is not a single statement, so it carries only the table:

```csharp
        await _clientWrapper.ExecuteAsync(
                batch,
                new ScyllaOperationContext(
                    "BATCH", operations[0].PartitionKey.Keyspace, operations[0].PartitionKey.Table),
                cancellationToken)
            .ConfigureAwait(false);
```

- [ ] **Step 6: Pass the context from the DDL path**

In `ScyllaDatabaseCreator.cs`, add the using:

```csharp
using YC.EntityFrameworkCore.ScyllaDB.Diagnostics;
```

In `ExecuteDdlAsync`, replace the execute call so the operation name is derived from the DDL text:

```csharp
        var rowSet = await _clientWrapper.ExecuteAsync(
                new SimpleStatement(cql),
                new ScyllaOperationContext(null, Keyspace(_currentContext.Context.Model), null, cql),
                cancellationToken)
            .ConfigureAwait(false);
```

- [ ] **Step 7: Run the tests to verify they pass**

Run: `dotnet build YC.EntityFrameworkCore.ScyllaDB.slnx && dotnet test test/YC.EntityFrameworkCore.ScyllaDB.FunctionalTests --filter "FullyQualifiedName~TelemetryTests"`
Expected: build clean with zero warnings; 4 tests pass.

- [ ] **Step 8: Run both suites**

Run: `dotnet test test/YC.EntityFrameworkCore.ScyllaDB.UnitTests && dotnet test test/YC.EntityFrameworkCore.ScyllaDB.FunctionalTests`
Expected: everything passes. Nothing else calls the changed signatures, since the two-argument overload was kept.

- [ ] **Step 9: Commit**

```bash
git add src/YC.EntityFrameworkCore.ScyllaDB test/YC.EntityFrameworkCore.ScyllaDB.FunctionalTests/TelemetryTests.cs
git commit -m "feat: emit a trace span for every CQL statement"
```

---

### Task 3: Record the operation-duration histogram

**Files:**
- Modify: `test/YC.EntityFrameworkCore.ScyllaDB.FunctionalTests/TelemetryTests.cs`

**Interfaces:**
- Consumes: `ScyllaInstrumentation.RecordDuration`, already called from `ScyllaClientWrapper.ExecuteAsync` in Task 2.

Task 2 already records the histogram; this task proves it, which is what stops a later refactor from silently dropping it.

- [ ] **Step 1: Write the failing test**

Append to `TelemetryTests.cs`, and add `using System.Diagnostics.Metrics;` at the top:

```csharp
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

            await context.Readings
                .Where(r => r.SensorId == Guid.NewGuid())
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
```

- [ ] **Step 2: Run the tests**

Run: `dotnet test test/YC.EntityFrameworkCore.ScyllaDB.FunctionalTests --filter "FullyQualifiedName~TelemetryTests"`
Expected: PASS. If `Every_statement_records_its_duration` fails on the tag assertions, the `TagList` construction in `ScyllaInstrumentation.RecordDuration` is wrong — fix it there, not in the test.

- [ ] **Step 3: Commit**

```bash
git add test/YC.EntityFrameworkCore.ScyllaDB.FunctionalTests/TelemetryTests.cs
git commit -m "test: cover the operation-duration histogram"
```

---

### Task 4: Prove the query-text option and document the telemetry

**Files:**
- Modify: `test/YC.EntityFrameworkCore.ScyllaDB.FunctionalTests/TelemetryTests.cs`
- Modify: `README.md`

**Interfaces:**
- Consumes: everything from Tasks 1 to 3. No production code changes — the behaviour is already implemented; this task pins it and documents it.

- [ ] **Step 1: Write the failing test**

Append to `TelemetryTests.cs`:

```csharp
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

            await context.Readings
                .Where(r => r.SensorId == Guid.NewGuid())
                .ToListAsync(TestContext.Current.CancellationToken);
        }

        var span = Assert.Single(captured, a => (string?)a.GetTagItem("db.operation.name") == "SELECT");

        Assert.Null(span.GetTagItem("db.query.text"));
        Assert.Equal(Keyspace, span.GetTagItem("db.namespace"));
        Assert.Equal("readings", span.GetTagItem("db.collection.name"));
        Assert.NotNull(span.GetTagItem("cassandra.consistency.level"));
    }
```

- [ ] **Step 2: Run the test**

Run: `dotnet test test/YC.EntityFrameworkCore.ScyllaDB.FunctionalTests --filter "FullyQualifiedName~Query_text_can_be_turned_off"`
Expected: PASS. Task 2 implemented the option; this pins it so a later refactor cannot quietly start leaking the text again.

If it fails, the fix belongs in `ScyllaClientWrapper.ExecuteAsync` — the `QueryText` must be cleared from the context *before* `StartActivity`, not after.

- [ ] **Step 3: Document it in the README**

Add a section after "Seeing the CQL":

```markdown
### Telemetry

The provider emits an `ActivitySource` and a `Meter`, both named `YC.EntityFrameworkCore.ScyllaDB`. Point
OpenTelemetry at them:

```csharp
.AddSource("YC.EntityFrameworkCore.ScyllaDB")
.AddMeter("YC.EntityFrameworkCore.ScyllaDB")
```

Every CQL statement produces a client span following the
[Cassandra semantic conventions](https://opentelemetry.io/docs/specs/semconv/db/cassandra/) — `db.namespace`,
`db.collection.name`, `db.operation.name`, `db.query.text`, `cassandra.consistency.level` — and a
`db.client.operation.duration` measurement, which carries rate, errors and latency together.

The query text contains no values: everything is bound through `?` markers. It does expose keyspace, table and
column names, so `WithQueryTextInTelemetry(false)` turns it off where schema names are themselves sensitive.

Nothing listening means no span is created and no measurement recorded, so this costs nothing when unobserved.
```

- [ ] **Step 4: Run everything**

Run: `dotnet build YC.EntityFrameworkCore.ScyllaDB.slnx && dotnet test test/YC.EntityFrameworkCore.ScyllaDB.UnitTests && dotnet test test/YC.EntityFrameworkCore.ScyllaDB.FunctionalTests`
Expected: build clean with zero warnings; every test passes.

- [ ] **Step 5: Commit**

```bash
git add test/YC.EntityFrameworkCore.ScyllaDB.FunctionalTests/TelemetryTests.cs README.md
git commit -m "docs: document the provider's telemetry"
```

---

## Self-review notes

**Spec coverage.** Emission mechanism → Task 1. Operation context → Tasks 1 and 2. Span attributes → Task 2, including `cassandra.consistency.level` from `ExecutionInfo.AchievedConsistency` and `db.response.returned_rows` from `RowSet.GetAvailableWithoutFetching()`. Metric → Tasks 1 and 3. Cost when unobserved → asserted in Task 1, Step 1. Configuration → Task 1, Steps 5 and 6; behaviour asserted in Task 4. Error handling → Task 2, Step 3, asserted in Tasks 2 and 3. Testing → Tasks 2, 3, 4. Documentation → Task 4, Step 3. Non-goals stay unimplemented.

**Design note on the row count.** An earlier draft had the client wrapper hand its `Activity` back to `ToPageAsync` so the caller could attach a row count after materialising the page. That was wrong twice over: the span has already stopped by the time `ExecuteAsync` returns, and handing a live `Activity` across a method boundary makes disposal someone else's problem. `RowSet.GetAvailableWithoutFetching()` reports the rows buffered from the response without advancing the enumerator, so the wrapper sets the attribute itself and no caller needs to know a span exists. Do not reintroduce an activity-returning overload.
