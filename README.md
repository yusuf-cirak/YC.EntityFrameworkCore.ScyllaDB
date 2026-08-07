# YC.EntityFrameworkCore.ScyllaDB

[![CI](https://github.com/yusuf-cirak/YC.EntityFrameworkCore.ScyllaDB/actions/workflows/ci.yml/badge.svg)](https://github.com/yusuf-cirak/YC.EntityFrameworkCore.ScyllaDB/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/YC.EntityFrameworkCore.ScyllaDB.svg)](https://www.nuget.org/packages/YC.EntityFrameworkCore.ScyllaDB)
[![Downloads](https://img.shields.io/nuget/dt/YC.EntityFrameworkCore.ScyllaDB.svg)](https://www.nuget.org/packages/YC.EntityFrameworkCore.ScyllaDB)
[![License](https://img.shields.io/github/license/yusuf-cirak/YC.EntityFrameworkCore.ScyllaDB.svg)](LICENSE)

A [ScyllaDB](https://www.scylladb.com/) database provider for Entity Framework Core, built directly on the
official [ScyllaDB C# driver](https://github.com/scylladb/csharp-driver).

> **Status: under construction.** Nothing works yet. The public API described below is the target, not the
> current state. Follow the [releases](https://github.com/yusuf-cirak/YC.EntityFrameworkCore.ScyllaDB/releases).

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
| `Skip(n)` | No `OFFSET`. Use cursor paging via `ToPageAsync`. |
| Arbitrary `Where` | Partition key by `=`/`IN`, clustering keys as a contiguous prefix. Anything else needs an index or `ALLOW FILTERING`. |
| Arbitrary `OrderBy` | Clustering columns only, in declared order or its exact reverse. |
| `GroupBy` | Only on a prefix of the primary key. |
| Transactions | None. Same-partition writes batch atomically; that is the ceiling. |
| Auto-increment keys | No sequences. Use `Guid` or `TimeUuid`, generated client-side. |
| "0 rows affected → conflict" | Writes are blind upserts with no affected-row count. Optimistic concurrency runs on lightweight transactions instead. |

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
        e.ToTable("readings");
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
