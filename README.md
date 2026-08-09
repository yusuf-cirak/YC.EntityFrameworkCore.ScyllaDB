# YC.EntityFrameworkCore.ScyllaDB

[![CI](https://github.com/yusuf-cirak/YC.EntityFrameworkCore.ScyllaDB/actions/workflows/ci.yml/badge.svg)](https://github.com/yusuf-cirak/YC.EntityFrameworkCore.ScyllaDB/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/YC.EntityFrameworkCore.ScyllaDB.svg)](https://www.nuget.org/packages/YC.EntityFrameworkCore.ScyllaDB)
[![Downloads](https://img.shields.io/nuget/dt/YC.EntityFrameworkCore.ScyllaDB.svg)](https://www.nuget.org/packages/YC.EntityFrameworkCore.ScyllaDB)
[![License](https://img.shields.io/github/license/yusuf-cirak/YC.EntityFrameworkCore.ScyllaDB.svg)](LICENSE)

A [ScyllaDB](https://www.scylladb.com/) database provider for Entity Framework Core, built directly on the
official [ScyllaDB C# driver](https://github.com/scylladb/csharp-driver).

> **Status: pre-1.0.** The provider creates schema, writes, queries, pages, scaffolds a model from an existing
> keyspace, and emits telemetry, with 104 unit tests and 48 functional tests passing against a real node. The
> public API can still move before a 1.0 release. Follow the
> [releases](https://github.com/yusuf-cirak/YC.EntityFrameworkCore.ScyllaDB/releases).

## Requirements

> - .NET 10 or later
> - EF Core 10
> - ScyllaDB (Apache Cassandra mostly works, but is not tested or guaranteed)

## What this is

A real EF Core provider, not a wrapper. It plugs into EF Core's own model building, change tracking, query
pipeline, and `SaveChangesAsync` — then translates all of it to CQL.

It is built the way the Cosmos provider is built: on EF Core's core abstractions, with its own query pipeline
and its own expression tree. It deliberately does **not** build on `Microsoft.EntityFrameworkCore.Relational`,
because that package assumes SQL — joins, subqueries, `OFFSET`, transactions — and CQL has none of those. Sitting
on it would mean unsupported queries fail at execution time instead of at translation time.

## What CQL cannot do

These are database limits, not gaps in the provider. The provider surfaces each one as a translation-time error
that names the problem and the fix.

| You might expect | CQL reality |
|---|---|
| `Join`, `Include`, navigations | No joins, no foreign keys. Denormalize, or use a materialized view. |
| `Skip(n)` | No `OFFSET`, because rows are reached by seeking rather than by counting past the ones before them. Use `ToPageAsync`. |
| Arbitrary `Where` | Partition key by `=`/`IN`, clustering keys as a contiguous prefix. Anything else needs an index or `ALLOW FILTERING`. |
| Arbitrary `OrderBy` | Clustering columns only, in declared order or its exact reverse. |
| `GroupBy` | Only on a prefix of the primary key. |
| Transactions | None. Same-partition writes batch atomically; that is the ceiling. |
| Auto-increment keys | No sequences. Use `Guid` or `TimeUuid`, generated client-side. |
| "0 rows affected → conflict" | Writes are blind upserts with no affected-row count. Optimistic concurrency runs on lightweight transactions instead. |

## Sessions and keyspaces

The driver's session is designed to be long-lived and shared, so the provider keeps one per distinct connection
configuration and every context built on the same settings reuses it. EF Core keys its internal service provider
the same way, and the keyspace is part of that configuration — so each keyspace gets its own service provider and
its own session. That is fine for the handful of keyspaces an application normally uses; if you need dozens,
build the session yourself and pass it in with the `ISession` overload.

## Async only

Every synchronous EF Core API throws. The driver is natively asynchronous, and sync-over-async buys nothing but
blocked threads and deadlock risk. Use `ToListAsync()`, `FirstOrDefaultAsync()`, `SaveChangesAsync()`. This
follows the precedent EF Core 9 set for the Cosmos provider.

## Install

```bash
dotnet add package YC.EntityFrameworkCore.ScyllaDB
```

## Quick start

```csharp
services.AddDbContext<MetricsContext>(options =>
    options.UseScyllaDb(["127.0.0.1"], keyspace: "metrics"));
```

```csharp
public class Reading
{
    public Guid SensorId { get; set; }
    public DateTimeOffset RecordedAt { get; set; }
    public double Value { get; set; }
}

protected override void OnModelCreating(ModelBuilder modelBuilder)
{
    modelBuilder.Entity<Reading>(e =>
    {
        e.ToScyllaTable("readings");
        e.HasPartitionKey(r => r.SensorId);
        e.HasClusteringKey(r => r.RecordedAt, ScyllaClusteringOrder.Descending);
    });
}
```

```csharp
await context.Database.EnsureCreatedAsync();

var recent = await context.Readings
    .Where(r => r.SensorId == sensorId && r.RecordedAt > cutoff)
    .Take(100)
    .ToListAsync();
```

### Paging

```csharp
var page = await context.Readings
    .Where(r => r.SensorId == sensorId)
    .ToPageAsync(pageSize: 100, continuationToken);
```

`page.ContinuationToken` fetches the next page. It is **not signed or encrypted** — a tampered token can read
other partitions, so sign it before handing it to an untrusted client.

### Materialized views

The answer to "the same rows, keyed differently" — ScyllaDB keeps the view in step with its base table, so the
view is read-only and writing to it throws.

```csharp
modelBuilder.Entity<ReadingByStatus>(e =>
{
    e.ToMaterializedView("readings_by_status", baseTableName: "readings");
    e.HasPartitionKey(r => r.Status);
    e.HasClusteringKey(r => r.RecordedAt, ScyllaClusteringOrder.Descending);
});
```

### Generating a model from an existing keyspace

```csharp
var source = await ScyllaScaffolder.GenerateAsync(session, "metrics");
```

Returns C# for the entity types and a `DbContext`, read from `system_schema`. It is deliberately not wired into
`dotnet ef dbcontext scaffold`: EF Core's reverse-engineering pipeline lives in the relational package and is
built around tables, columns and foreign keys, with no way to express a partition key or a clustering order —
the parts that decide whether a CQL table can be queried at all.

### Seeing the CQL

```csharp
query.ToCqlString();
```

Worth checking: in CQL the difference between a partition seek and a cluster-wide scan is which restrictions
reached the server.

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

## Building and testing

```bash
dotnet build YC.EntityFrameworkCore.ScyllaDB.slnx
dotnet test test/YC.EntityFrameworkCore.ScyllaDB.UnitTests        # no database required
dotnet test test/YC.EntityFrameworkCore.ScyllaDB.FunctionalTests  # requires Docker
```

Functional tests start a real ScyllaDB node with [Testcontainers](https://dotnet.testcontainers.org/), so Docker
must be running.

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md).

## License

MIT — see [LICENSE](LICENSE).
