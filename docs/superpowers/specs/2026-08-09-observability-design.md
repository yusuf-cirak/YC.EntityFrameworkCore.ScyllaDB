# Observability for YC.EntityFrameworkCore.ScyllaDB

Date: 2026-08-09

## Context

The provider currently emits almost nothing. `ScyllaLoggingDefinitions` is an empty shell that exists only
because EF Core requires every provider to supply one, and there is a single `ILogger.LogWarning` call for the
multi-partition `SaveChangesAsync` case. Nothing reports what CQL ran, how long it took, or whether it failed.

That gap matters more here than it would for a relational provider. In CQL the difference between a partition
seek and a cluster-wide scan is invisible from the outside — both return rows — so without telemetry there is no
way to tell a healthy query from one that is about to become a production incident.

OpenTelemetry defines semantic conventions for Cassandra client operations, and ScyllaDB speaks CQL, so there is
a real standard to target rather than a bespoke schema.

## Goals

Emit standard database telemetry — traces and metrics — that existing OpenTelemetry tooling understands without
any provider-specific configuration beyond naming the source.

## Non-goals

- **ScyllaDB cost signals.** Signals a generic instrumentation cannot produce — `ALLOW FILTERING` scans,
  non-atomic multi-partition saves, lightweight-transaction rejection rates, batch shapes — are deliberately out
  of scope for this iteration. The design does not preclude them; they would add attributes and events to the
  same `ActivitySource`.
- **Cluster topology signals.** Coordinator node and data centre, speculative execution counts, hosts tried.
- **Provider internals.** Prepared-statement cache hit rate, session establishment, schema-agreement waits.
- **Parameter values.** `db.query.parameter.<key>` is opt-in in the semantic conventions and will not be emitted
  at all.

## Emission mechanism

Native `ActivitySource` and `Meter`, both from `System.Diagnostics.DiagnosticSource`, which already ships as a
dependency of `Microsoft.EntityFrameworkCore`. No new package reference.

This makes OpenTelemetry a *listener* rather than a dependency: a consumer calls
`AddSource("YC.EntityFrameworkCore.ScyllaDB")` and `AddMeter("YC.EntityFrameworkCore.ScyllaDB")` and receives
everything. `dotnet-trace` and `dotnet-counters` work against the same sources with no extra wiring, and a
consumer using neither pays nothing.

## Architecture

### Instrumentation point

Every CQL statement the provider sends already passes through one method:
`ScyllaClientWrapper.ExecuteAsync(IStatement, CancellationToken)`. Queries, writes, batches, DDL, lightweight
transactions and connectivity checks all route through it. Instrumentation goes there and nowhere else.

### Operation context

The client wrapper receives an `IStatement`, which does not carry the keyspace, table, or operation kind in a
form worth parsing out of the CQL text. The call sites do know them:

- `ScyllaQueryingEnumerable` holds `CqlSelectExpression.EntityType`
- `ScyllaDatabaseWrapper` holds `ScyllaWriteOperation.PartitionKey`, which carries keyspace and table
- `ScyllaDatabaseCreator` knows the keyspace and the DDL kind

So `IScyllaClientWrapper.ExecuteAsync` gains an optional `ScyllaOperationContext` parameter — a
`readonly record struct` carrying operation name, keyspace, table and batch size. Existing raw calls that omit
it still produce a span, with fewer attributes.

### Components

| Component | Responsibility |
|---|---|
| `ScyllaInstrumentation` (static) | Owns the `ActivitySource` and `Meter`, both named `YC.EntityFrameworkCore.ScyllaDB`, versioned from the assembly. Exposes activity creation and duration recording. |
| `ScyllaOperationContext` (readonly record struct) | Carries what the call site knows: operation name, keyspace, table, batch size. |
| `ScyllaClientWrapper.ExecuteAsync` | Starts the activity, executes, populates attributes, records the histogram, and propagates any exception unchanged. |

## Traces

Span name follows the general database convention: `{db.operation.name} {db.namespace}.{db.collection.name}`,
for example `SELECT metrics.readings`. Kind is `Client`.

When no table applies — creating a keyspace, or a raw statement executed without an operation context — the name
drops the missing parts: `CREATE KEYSPACE metrics`, or just the operation name when the keyspace is unknown too.
Span names must stay low-cardinality, so they never include anything beyond these.

| Attribute | Value |
|---|---|
| `db.system.name` | `cassandra` |
| `db.namespace` | Keyspace |
| `db.collection.name` | Table, for single-table operations |
| `db.operation.name` | `SELECT`, `INSERT`, `UPDATE`, `DELETE`, `BATCH`, `CREATE TABLE`, … |
| `db.query.text` | The CQL, when enabled |
| `cassandra.consistency.level` | Only when explicitly configured, since otherwise the driver's default applies and the provider does not know it |
| `cassandra.page.size` | Only when set |
| `cassandra.query.idempotent` | The statement's idempotence flag |
| `db.operation.batch.size` | Statement count, for batches |
| `error.type` | Exception type name, on failure |
| `db.response.status_code` | Driver error code where the exception exposes one |

`db.system.name` is `cassandra` because the semantic conventions define no separate ScyllaDB value, and ScyllaDB
is CQL-compatible — a consumer's Cassandra dashboards should apply unchanged.

`db.response.returned_rows` is set only in `ToPageAsync`, where the rows are already materialised into a list.
Ordinary query results are streamed, so counting them would force materialisation purely to report a number.

## Metrics

One instrument: `db.client.operation.duration`, a histogram in seconds, as the semantic conventions define.

Attributes: `db.system.name`, `db.namespace`, `db.collection.name`, `db.operation.name`, and `error.type` when
the operation failed.

A single histogram answers rate, errors and duration together. Separate counters would be derivable from it and
would only add cardinality.

## Cost when unobserved

`ActivitySource.StartActivity` returns null when nothing is listening, and every attribute is computed inside
the null check. A histogram with no listener records nothing. Instrumentation is therefore always on, with no
option to disable it, because there is nothing to save by disabling it.

## Configuration

`ScyllaDbContextOptionsBuilder.WithQueryTextInTelemetry(bool enabled = true)` — default on.

The provider binds every value through `?` markers, so the CQL text contains no literals; what it does expose is
keyspace, table and column names. That is the single most useful field for explaining why a query is slow, so it
is on by default, with the option for environments where schema names are themselves sensitive.

## Error handling

On exception the activity status is set to `Error`, `error.type` is set to the exception type name, and the
exception propagates unchanged. Telemetry never swallows or alters a failure, and never throws one of its own —
a failure to record must not become a failure to query.

## Testing

Functional tests against the Testcontainers node, using `ActivityListener` and `MeterListener` to capture what
was emitted. The instrumentation lives inside the client wrapper, so a unit test with a fake wrapper would
verify nothing real.

Coverage:

- A query emits one span named `SELECT <keyspace>.<table>` carrying the expected attributes
- A write emits `INSERT`, a multi-statement single-partition save emits `BATCH` with `db.operation.batch.size`
- A failing statement sets `error.type` and leaves the exception unchanged
- `db.client.operation.duration` records one measurement per execution, with matching attributes
- `WithQueryTextInTelemetry(false)` omits `db.query.text` while leaving the rest intact

## Documentation

A README section showing the two lines a consumer needs:

```csharp
.AddSource("YC.EntityFrameworkCore.ScyllaDB")
.AddMeter("YC.EntityFrameworkCore.ScyllaDB")
```

## References

- [Semantic conventions for Cassandra client operations](https://opentelemetry.io/docs/specs/semconv/db/cassandra/)
- [Semantic conventions for database client spans](https://opentelemetry.io/docs/specs/semconv/db/database-spans/)
