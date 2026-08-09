# Features

What the provider supports, what it refuses, and why. Refusals happen while the query is being translated, not
when it runs, and each one names the alternative.

Most of the "not supported" entries are CQL, not gaps in the provider. They are marked accordingly:

- **CQL** — the database cannot do this. No version of this provider will add it.
- **Not yet** — the provider could do this and does not.

## Contents

- [Modelling](#modelling) · [Schema](#schema) · [Writing](#writing) · [Querying](#querying)
- [Telemetry](#telemetry) · [Scaffolding](#scaffolding) · [Configuration](#configuration)

## The example model

Every snippet below maps this shape:

```csharp
public class Reading
{
    public Guid SensorId { get; set; }
    public DateTimeOffset RecordedAt { get; set; }
    public double Value { get; set; }
    public string Status { get; set; } = "ok";
}
```

## Modelling

| Feature | Fluent | Attribute |
|---|---|---|
| Table and keyspace | `e.ToScyllaTable("readings", keyspace: "metrics")` | `[ScyllaTable("readings", Keyspace = "metrics")]` |
| Partition key | `e.HasPartitionKey(r => r.SensorId)` | `[PartitionKey]` |
| Composite partition key | `e.HasPartitionKey(r => new { r.SensorId, r.Bucket })` | `[PartitionKey(order)]` |
| Clustering key | `e.HasClusteringKey(r => r.RecordedAt, ScyllaClusteringOrder.Descending)` | `[ClusteringKey(order, direction)]` |
| Static column | `e.Property(r => r.SensorName).IsStaticColumn()` | `[StaticColumn]` |
| Counter | `e.Property(p => p.Views).IsCounter()` | `[Counter]` |
| Explicit CQL type | `e.Property(r => r.Tags).HasCqlType("set<text>")` | `[CqlType("set<text>")]` |
| Collection kind | `.IsList()` / `.IsSet()` / `.IsMap()` | — |
| Secondary index | `e.HasScyllaIndex(r => r.Status, "readings_by_status")` | `[SecondaryIndex]` |
| Materialized view | `e.ToMaterializedView("readings_by_status", "readings")` | — |
| Table default TTL | `e.HasDefaultTimeToLive(TimeSpan.FromDays(30))` | — |
| Conditional insert | `e.UseInsertIfNotExists()` | — |
| Keyspace replication | `modelBuilder.HasKeyspaceReplication(ScyllaReplication.NetworkTopologyStrategy(("dc1", 3)))` | — |

The two styles produce identical DDL; a test asserts it, so neither is a trap.

### The primary key is physical layout, not a constraint

In CQL the primary key decides where a row lives and how it is ordered on disk: partition columns first in
declaration order, then clustering columns in theirs. The provider derives EF Core's key from your partition and
clustering declarations rather than letting EF discover one, because EF's own discovery would happily pick an
`Id` property and produce a table nobody can query.

It cannot be changed later. Altering a primary key means creating a new table and migrating the data.

### Rejected models

| Model | Why |
|---|---|
| No partition key | **CQL** — every table needs one; it is what decides which node owns a row. |
| Navigation properties | **CQL** — no joins, no foreign keys. Denormalise, or use a materialized view. |
| Static column with no clustering key | **CQL** — a partition would hold one row, so "once per partition" means nothing. |
| Counter mixed with a regular column | **CQL** — a table is either all counters plus its key, or none. |
| Counter in the primary key | **CQL** |
| Concurrency token without `UseInsertIfNotExists()` | **CQL** — Paxos orders lightweight transactions only against each other; mixing plain writes into the partition makes the check unsound |
| `IEnumerable<T>` with no collection kind | **Provider** — `list` and `set` are indistinguishable from the CLR type, and they behave differently. Say which with `.IsList()` or `.IsSet()`. |
| `ValueGeneratedOnAdd` on a numeric key | **CQL** — no sequences, no identity. Use `Guid` or `TimeUuid`, generated client-side. |

## Schema

`EnsureCreatedAsync` creates the keyspace, user-defined types, tables, secondary indexes and materialized views,
in that order — a view cannot be created before the table it reads from. `EnsureDeletedAsync` drops the keyspace.

Every DDL statement waits for the cluster to agree on the resulting schema before the next one runs. Schema
changes propagate eventually, so skipping that wait produces failures that look random: a table created against a
node that has not yet seen its keyspace. The wait is bounded by `WithSchemaAgreementTimeout`, default 10 seconds.

All identifiers are quoted. An unquoted CQL identifier folds to lower case, which would turn a `SensorId`
property into a `sensorid` column and break any hand-written CQL that quoted the name.

**Migrations: not yet.** Use `EnsureCreatedAsync`. When they arrive they will cover creating and dropping
keyspaces, tables, types, indexes and views, and adding or dropping columns — CQL has no `ALTER COLUMN TYPE`,
and a primary-key change will be a hard error rather than a silent rebuild.

## Writing

`SaveChangesAsync` groups by partition:

- Writes that share a partition go out as one `UNLOGGED` batch — atomic for that partition, and no more
  expensive than sending the statements separately.
- Writes to different partitions go out as independent statements, concurrently, bounded by
  `WithMaxConcurrentWrites` (default 32).

**A save spanning partitions is therefore not atomic.** That is CQL, not a shortcut, and the provider logs a
warning when it happens. `WithBatchMode(ScyllaBatchMode.Logged)` buys eventual all-or-nothing across partitions
at a steep price and still provides no isolation; `ScyllaBatchMode.Never` disables batching entirely.

Updates send only the columns that changed. A CQL update writes a cell per column, so rewriting untouched ones
costs needless compaction pressure.

### Time to live and write timestamps

```csharp
using (context.UseScyllaWriteOptions(new ScyllaWriteOptions(TimeToLive: TimeSpan.FromDays(7))))
{
    context.AddRange(readings);
    await context.SaveChangesAsync();
}
```

TTL is dropped from deletes — nothing is left to expire. A custom write timestamp is refused on a conditional
write, because lightweight transactions order themselves through Paxos and assign their own.

### Counters

A counter row cannot be inserted, only incremented into existence, so `Add` becomes an increment from zero and a
modification sends the *difference* against EF Core's original-value snapshot. That snapshot is what makes plain
`entity.Views += 7` work at all: CQL has no read-modify-write. Counter increments are not idempotent, so they are
never marked for driver-side retry.

### Optimistic concurrency

```csharp
e.Property(s => s.Version).IsConcurrencyToken();   // UPDATE ... IF "Version" = ?
e.UseInsertIfNotExists();                          // INSERT ... IF NOT EXISTS
```

Lightweight transactions are the only optimistic concurrency CQL can offer. An ordinary write is a blind upsert
that reports no affected-row count, so EF Core's usual "zero rows changed means someone else won" has nothing to
observe. A rejected write raises `DbUpdateConcurrencyException` carrying the values that won — for a failed
condition that is the primary key plus the columns the condition named, not the whole row, because that is all
the server returns.

They cost roughly four times an ordinary write and are confined to a single partition, so they are never batched.

**A concurrency token requires `UseInsertIfNotExists()`, and the model validator enforces it.** ScyllaDB orders
lightweight transactions only against each other; a partition that is also written by ordinary statements sits
outside that guarantee, so a later `IF` can match a value the row has already moved past and the check passes
when it should have failed. The failure is silent and intermittent — the worst kind — which is why the pairing is
required rather than suggested.

## Querying

| Supported | Notes |
|---|---|
| `Where` | Partition key by `==` or `Contains` (→ `IN`); clustering keys as a contiguous prefix, with at most one trailing range |
| `OrderBy` / `OrderByDescending` | Clustering columns only, in the declared order or its exact reverse |
| `Take` | → `LIMIT` |
| `First` / `FirstOrDefault` / `Single` / `SingleOrDefault` | |
| `Count` / `LongCount` / `Any` | → `COUNT(*)` |
| `Select` | Projections narrow the selected columns |
| `PerPartitionLimit` | → `PER PARTITION LIMIT`, applied before `LIMIT` |
| `ToPageAsync` | Cursor paging |
| `EF.Functions.AllowFiltering()` | Opts into a scan |
| `ToCqlString()` | Shows the statement without running it |

### Restrictions CQL enforces

The validator runs once over the finished query, not per clause, because restrictions accumulate across several
`Where` calls — `Where(a).Where(c)` is innocent term by term and invalid together.

| Rule | Reason |
|---|---|
| The whole partition key must be restricted | Otherwise the query touches every node. Opt out with `AllowFiltering`. |
| Partition keys take `==` or `IN` only | A partition key is hashed, not ordered, so a range cannot be answered. |
| Clustering columns form a contiguous prefix | Rows are stored in clustering order; the engine cannot seek past a gap. |
| At most one range, on the last restricted clustering column | |
| `ORDER BY` on clustering columns, in order or exactly reversed | Rows can be walked forwards or backwards and nothing else. |
| Non-key columns need an index or `AllowFiltering` | |
| One secondary index per query | ScyllaDB will not combine two. |

### Not translated

| Construct | Why |
|---|---|
| `Join`, `Include`, navigations | **CQL** — denormalise, or use a materialized view |
| `Skip` | **CQL** — no `OFFSET`. Use `ToPageAsync` |
| `GroupBy` | **CQL** — only on a primary-key prefix, aggregated on the coordinator |
| `Union`, `Except`, `Intersect`, `Concat` | **CQL** |
| Subqueries | **CQL** |
| `OR` in a predicate | **CQL** — run one query per branch |
| `!=` | **CQL** |
| `Sum`, `Min`, `Max`, `Average`, `All` | **CQL** — coordinator-side and unbounded outside one partition |
| Transactions | **CQL** — same-partition batching is the ceiling |
| `Distinct`, `Reverse`, `Last`, `OfType`, `DefaultIfEmpty` | **Not yet** |
| `ExecuteUpdate` / `ExecuteDelete` | **CQL** — no update or delete by a non-key predicate |
| `FromSql` | **Not yet** — reach the driver through `IScyllaClientWrapper` |

### Limiting per partition

```csharp
await context.Readings
    .Where(r => sensorIds.Contains(r.SensorId))
    .PerPartitionLimit(3)
    .ToListAsync();
```

The one thing `Take` cannot express. "The newest three readings from every sensor" is a single query with this
and one query per sensor without it, because a plain `LIMIT` would stop after three rows in total. It only earns
its keep on a query that reads more than one partition; within a single partition it does the same job as
`Take`.

### Async only

Every synchronous EF Core API throws. The driver's I/O is natively asynchronous, so sync-over-async would block a
thread for no benefit and risk deadlocks. This follows the precedent EF Core 9 set for the Cosmos provider.

One subtlety: `SaveChanges()` with nothing tracked returns 0 rather than throwing, because EF Core short-circuits
before reaching the provider.

### Paging

```csharp
var page = await query.ToPageAsync(pageSize: 100, continuationToken);
```

Loop until `ContinuationToken` is null, never until a page comes back short — the coordinator can hand back a
token it cannot yet prove is exhausted, so the last page is often empty.

**The token is not signed or encrypted.** It wraps the server's raw paging state, and a tampered token can read
other partitions. Sign it before handing it to an untrusted client, and verify it on the way back.

## Telemetry

```csharp
.AddSource("YC.EntityFrameworkCore.ScyllaDB")
.AddMeter("YC.EntityFrameworkCore.ScyllaDB")
```

Every statement produces a client span following the
[Cassandra semantic conventions](https://opentelemetry.io/docs/specs/semconv/db/cassandra/), and a
`db.client.operation.duration` measurement.

| Attribute | Source |
|---|---|
| `db.system.name` | Always `cassandra` — the conventions define no ScyllaDB value |
| `db.namespace` | Keyspace |
| `db.collection.name` | Table, when the statement targets exactly one |
| `db.operation.name` | `SELECT`, `INSERT`, `BATCH`, `CREATE TABLE`, … |
| `db.query.text` | The CQL, with `?` markers. Disable with `WithQueryTextInTelemetry(false)` |
| `cassandra.consistency.level` | The level actually achieved, not merely the one configured |
| `cassandra.page.size`, `cassandra.query.idempotent` | |
| `db.operation.batch.size`, `db.response.returned_rows` | |
| `error.type` | Exception type, on failure |

Parameter values are never emitted. Nothing listening means no span is created and no measurement recorded, so
this costs nothing unobserved. Telemetry never throws, never swallows an application exception and never alters
one — a failure to observe must not become a failure to query.

## Scaffolding

```csharp
var source = await ScyllaScaffolder.GenerateAsync(session, "metrics");
```

Reads `system_schema` and returns C# for the entity types and a `DbContext`, including partition and clustering
keys, clustering order, static columns, counters, collection kinds and materialized views.

It is deliberately not wired into `dotnet ef dbcontext scaffold`. EF Core's reverse-engineering pipeline lives in
`Microsoft.EntityFrameworkCore.Relational` — the package this provider exists to avoid — and models tables,
columns and foreign keys, with no way to express a partition key or a clustering order. A model produced through
it would be missing exactly the parts that decide whether a CQL table can be queried.

Types it cannot map — user-defined types, tuples, vectors — are emitted as `object` with a comment naming the CQL
type, rather than guessed at.

## Configuration

```csharp
options.UseScyllaDb(["10.0.0.1", "10.0.0.2"], keyspace: "metrics", o => o
    .WithPort(9042)
    .WithCredentials(user, password)
    .WithSsl()
    .WithConsistencyLevel(ConsistencyLevel.LocalQuorum)
    .WithPageSize(1000)
    .WithExecutionProfile("oltp")
    .WithQueryTimeout(TimeSpan.FromSeconds(5))
    .WithSchemaAgreementTimeout(TimeSpan.FromSeconds(10))
    .WithBatchMode(ScyllaBatchMode.PerPartitionUnlogged)
    .WithMaxConcurrentWrites(32)
    .WithQueryTextInTelemetry()
    .WithTracing());
```

Also `UseScyllaDb(cluster, keyspace, …)` and `UseScyllaDb(session, …)` for callers who build their own cluster —
custom retry policies, load balancing, or an existing session.

Contact points are host names or addresses only. The driver applies one port to the whole cluster, so a
`"host:port"` contact point is rejected with a message pointing at `WithPort`.

**Consistency is left unset by default**, so the driver's own `LOCAL_ONE` applies. The provider does not impose a
default of its own, because doing so would silently change the durability characteristics of every query.

### Sessions and keyspaces

A driver session is thread-safe and meant to live as long as the application, so the provider keeps one per
distinct connection configuration and every context built on the same settings reuses it. EF Core keys its
internal service provider the same way, and the keyspace is part of that configuration — so each keyspace gets
its own service provider and its own session. Fine for the handful an application normally uses; if you need
dozens, build the session yourself and pass it in.
