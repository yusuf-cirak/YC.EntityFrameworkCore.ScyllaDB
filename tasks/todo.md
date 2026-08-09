# YC.EntityFrameworkCore.ScyllaDB — progress

Plan: `~/.claude/plans/scylladbcsharpdriver-n-zerine-kurulu-htt-valiant-pudding.md`

## Phase 0 — Repo scaffolding ✅

- [x] `Directory.Build.props` — net10.0, nullable, warnings-as-errors, package metadata, release-please marker
- [x] `Directory.Packages.props` — central package management, verified versions
- [x] `global.json` (SDK 10.0.301), `NuGet.config`, `codecov.yml`, `.gitignore`
- [x] `LICENSE` (MIT), `README.md`, `CONTRIBUTING.md`, `SECURITY.md`
- [x] `release-please-config.json`, `.release-please-manifest.json`
- [x] `.github/` — CODEOWNERS, PR template, dependabot, issue templates, `ci.yml`, `release.yml`
- [x] Projects: `src/`, `test/…UnitTests`, `test/…FunctionalTests`, `samples/ScyllaDB.Sample`, wired into `.slnx`
- [x] Driver compatibility tests on net10 — 7/7 passing

**Verified:** `dotnet build` → 5 projects, 0 errors, 0 warnings. `dotnet pack` → `.nupkg` + `.snupkg`.

**Findings:**
- Driver assembly is named `ScyllaDB`, not `Cassandra` — it does *not* collide with `CassandraCSharpDriver` on
  assembly identity, only on type names within the shared `Cassandra` namespace.
- Driver works on net10 despite shipping only netstandard2.0: `TimeUuid`, `LocalDate`, `LocalTime`, `Duration`,
  `BigInteger`, `IPAddress` all resolve and behave.
- `QueryOptions.DefaultConsistencyLevel` is `LocalOne`; serial default is `Serial`.

**Deferred:** `buildTransitive/*.props` waits for `ScyllaDesignTimeServices` to exist.

## Phase 1 — Options, session, DI, async guards ✅

- [x] `ScyllaOptionsExtension` + `ExtensionInfo` (service-provider hash covers every connection-shaping value)
- [x] `ScyllaDbContextOptionsBuilder` and the `UseScyllaDb` overloads (contact points, `ICluster`, `ISession`)
- [x] `ScyllaSingletonOptions` / `IScyllaSingletonOptions`
- [x] `ScyllaSingletonClientWrapper` (singleton `ICluster` + `ISession`, prepared-statement cache)
- [x] `ScyllaClientWrapper` (scoped facade, applies statement defaults)
- [x] `ScyllaTransactionManager` — every transaction member throws, reset stays silent
- [x] `ScyllaStrings`, `ScyllaLoggingDefinitions`, `ScyllaExecutionStrategyFactory`
- [x] `AddEntityFrameworkScyllaDb` — service graph resolves end to end
- [x] Functional: container fixture + `CanConnectAsync` against a real node

**Verified:** build clean across 5 projects, 0 warnings. 35 unit tests, 3 functional tests against a real
`scylladb/scylla:6.2` container — all passing.

**Findings:**
- `IQueryContextFactory`, `IQueryableMethodTranslatingExpressionVisitorFactory` and
  `IShapedQueryCompilingExpressionVisitorFactory` have no core defaults — a context cannot even be *built*
  without them. The two visitor factories are registered as stubs that throw from `Create()`, which EF only
  calls when a query compiles. Contexts stay constructible; querying fails with a clear message.
- `SaveChanges()` with nothing tracked never reaches the provider, so it does not throw on this async-only
  provider. Pinned by a test so it does not read as a bug later.
- The driver treats a contact point as a host name only, so `"host:port"` fails DNS resolution with a message
  that hides the real mistake. `UseScyllaDb` now rejects it up front and points at `WithPort`.
- `IStatement` has no `SetExecutionProfile`; the profile is chosen per execution via
  `ISession.ExecuteAsync(statement, profileName)`.
- `NonRetryingExecutionStrategy` ships in core, so the provider needs no strategy of its own — the driver owns
  retries, and layering EF retries on top could duplicate non-idempotent writes undetectably.

## Phase 2 — Metadata, conventions, DDL, EnsureCreated ✅

- [x] Attributes and enums in the bare root namespace (`[ScyllaTable]`, `[PartitionKey]`, `[ClusteringKey]`,
      `[StaticColumn]`, `[Counter]`, `[CqlType]`), plus `ScyllaReplication`
- [x] `ToScyllaTable` / `HasPartitionKey` / `HasClusteringKey` / `HasDefaultTimeToLive`, keyspace replication,
      `IsStaticColumn` / `IsCounter` / `HasCqlType` / `IsList` / `IsSet` / `IsMap`
- [x] Annotation names and metadata read/write extensions
- [x] `ScyllaModelFinalizingConvention` — attributes, primary key rule, ambiguous-collection check
- [x] `ScyllaModelValidator` — keyspace, navigations, partition key, static columns, counters, type mappings
- [x] `ScyllaTypeMappingSource` — the scalar CQL table, enums, and collections
- [x] `IScyllaDdlGenerator` + `EnsureCreatedAsync` / `EnsureDeletedAsync` with schema-agreement waiting

**Verified:** build clean across 5 projects, 0 warnings. 56 unit tests; 7 functional tests against a real
container, including reading column kinds and clustering order back out of `system_schema`.

**Findings:**
- Type mapping had to come forward from its own phase: DDL cannot emit `CREATE TABLE` without knowing each
  column's CQL type.
- The session is no longer bound to a keyspace. Binding it made `EnsureCreatedAsync` impossible — connecting
  would fail before the keyspace it was meant to create existed. Every statement is keyspace-qualified instead,
  which is unambiguous anyway. `CanConnectAsync` now checks `system_schema.keyspaces` explicitly.
- An unmappable collection member never becomes an EF property, so neither `ModelValidator` nor its
  `ThrowPropertyNotMappedException` hook ever sees it — verified empirically. The check lives in the
  model-finalizing convention, where the CLR member is still in view.
- The keyspace named in `UseScyllaDb` now becomes the model's default, so it need not be repeated in
  `OnModelCreating`.
- Scylla reports `clustering_order` as `DESC`; Cassandra's documentation shows `desc`. Compared case-insensitively.
- All identifiers are quoted, because an unquoted CQL identifier folds to lower case and would silently turn
  `SensorId` into `sensorid`.

## Phase 3 — SaveChanges ✅

- [x] `ScyllaDmlGenerator` — insert, update (changed columns only), delete, counter increment
- [x] `ScyllaPartitionKey` — value equality, the grouping key for batching
- [x] Grouping: same-partition unlogged batch, cross-partition concurrent statements, logged batch opt-in,
      `ScyllaBatchMode.Never`
- [x] Prepared statements via `BindAsync`, so the driver derives routing keys itself
- [x] Idempotence flags, counter batches kept separate

**Verified:** build clean across 5 projects, 0 warnings. 64 unit tests, 14 functional tests against a real
container — insert, selective update, delete, single-partition batch, cross-partition writes, logged batch,
and a counter accumulating across two saves.

**Findings:**
- Counters cannot be inserted, so `Added` becomes an increment from zero, and `Modified` sends the *difference*
  against EF Core's original-value snapshot. That snapshot is what makes plain `entity.Views += 7` work at all,
  since CQL has no read-modify-write.
- A counter increment is not idempotent — replaying it adds the delta twice — so only non-counter statements
  are marked idempotent for driver-side retry.
- Manual routing keys turned out unnecessary: preparing a statement lets the driver compute the routing key
  from the bound partition key, which is what gets a write to the owning shard.
- `EntityEntry` exposes its `IUpdateEntry` through `IInfrastructure<InternalEntityEntry>`, which makes DML
  generation testable as a pure function with no driver involved.

## Phase 4 — Query pipeline ✅

- [x] `CqlSelectExpression` with typed restriction buckets, not a boolean predicate tree
- [x] `ScyllaQueryableMethodTranslatingExpressionVisitor` — Where, OrderBy/ThenBy, Take, First, Single, Any,
      Count/LongCount, Contains → `IN`; every other operator throws with a named reason
- [x] `CqlQueryValidator` — partition key coverage, contiguous clustering prefix, single trailing range,
      ordering restricted to clustering order or its exact reverse
- [x] `ScyllaShapedQueryCompilingExpressionVisitor` + `ScyllaQueryingEnumerable`, async-only
- [x] `EF.Functions.AllowFiltering()` plus the evaluatable-expression filter that keeps it out of client-side
      evaluation
- [x] `ToCqlString()` — shows the statement a query would run, without running it

**Verified:** build clean across 5 projects, 0 warnings. 83 unit tests, 25 functional tests against a real
container — partition-scoped reads, clustering ranges, `IN`, `Take`, `Count`/`Any`, ordering, captured
variables as parameters, `ALLOW FILTERING`, and the refusal of synchronous enumeration.

**Findings:**
- `EF.Functions.AllowFiltering()` mentions no entity, so EF Core evaluated it client-side and hit the
  "only for use inside a query" guard instead of translating it. An `IEvaluatableExpressionFilterPlugin` is what
  routes it to the translator.
- Projected columns follow EF Core's property order (keys first, then alphabetical), not declaration order,
  because the value buffer handed to the materialiser is addressed by property index.
- `InjectStructuralTypeMaterializers` plus a `ValueBuffer` filled by `property.GetIndex()` is the whole
  materialisation path — no custom materialiser needed.

**Not translated yet, each with its own message:** projections (`Select` of anything but the entity), `Skip`,
`GroupBy`, joins, set operations, `Sum`/`Min`/`Max`/`Average`/`All`, `Distinct`, `Reverse`, `Last`, `OR`, `!=`.

## Phase 5 — Scylla-specific writes ✅

- [x] `ScyllaWriteOptions` + `context.UseScyllaWriteOptions(...)` scope → `USING TTL` / `USING TIMESTAMP`
- [x] `IsConcurrencyToken()` → `UPDATE ... IF col = ?` and `DELETE ... IF col = ?`
- [x] `UseInsertIfNotExists()` → `INSERT ... IF NOT EXISTS`
- [x] Conditional writes execute standalone, `[applied]` checked, `DbUpdateConcurrencyException` carries the
      values that won

**Verified:** build clean across 5 projects, 0 warnings. 92 unit tests, 30 functional tests — a duplicate
conditional insert rejected, a stale concurrency token losing the race, a matching token applying, and rows
actually expiring when their time is up.

**Findings:**
- A failed `IF NOT EXISTS` still returns a row — the one that was already there — so "did a row come back?" is
  the wrong test for which failure occurred. The entity state distinguishes them.
- A failed conditional update returns the primary key plus only the columns the `IF` clause named, not the whole
  row, so the exception can quote the winning version but not the winning body.
- `USING` precedes `SET` in a CQL update, and bind markers are positional, so the TTL and timestamp values have
  to be added to the list before the assignments.
- CQL rejects a custom write timestamp on a conditional write, because Paxos assigns its own to keep ballots
  ordered. The provider refuses it up front with that explanation.
- TTL is not valid on a delete — there is nothing left to expire — so it is dropped there while the timestamp
  is kept.

**Skipped:** `WRITETIME()` / `TTL()` read functions, which need projections to be useful; per-entity write
options, since the scope plus table-level `default_time_to_live` cover the common cases.

## Phase 6 — Query ergonomics ✅

- [x] Projections — `Select(x => x.Value)` and `Select(x => new { ... })` narrow the selected columns
- [x] Cursor paging — `ToPageAsync(pageSize, continuationToken)` returning `ScyllaPage<T>`
- [x] Secondary indexes — `HasScyllaIndex(...)` / `[SecondaryIndex]`, DDL, and validator awareness

**Verified:** build clean across 5 projects, 0 warnings. 95 unit tests, 35 functional tests — projections
narrowing the `SELECT`, paging walking a result set without overlap, and an indexed column queried without
`ALLOW FILTERING`.

**Findings:**
- A secondary index makes a query legal *without* restricting the partition key — that is the whole point of
  one. The validator checked the partition key first and rejected such queries; index awareness has to come
  first.
- EF Core caches an internal service provider per distinct connection configuration, and the keyspace is part of
  that, so a keyspace per test built a service provider per test and tripped EF Core's own
  "more than twenty service providers" guard. Tests now use one keyspace per class with a fresh partition key per
  test. Documented in the README, since an application with dozens of keyspaces would hit the same wall.
- ScyllaDB uses at most one secondary index per query, which the validator now reports rather than letting the
  server refuse it.

**Skipped:** `PER PARTITION LIMIT` (no public API reaches it, so the generator support was removed rather than
left unreachable) and materialized views.

## Phase 7 — Materialized views and scaffolding ✅

- [x] `ToMaterializedView(view, baseTable)` — DDL with `IS NOT NULL` guards, created after its base table,
      writes refused
- [x] `ScyllaScaffolder.GenerateAsync(session, keyspace)` — reads `system_schema` and emits entity types plus a
      `DbContext`

**Verified:** build clean across 5 projects, 0 warnings. 96 unit tests, 39 functional tests — a view queried by
its own partition key, a view reflecting a write to its base table, a write to a view refused, and a scaffolding
round-trip that reproduces the keys and the view mapping.

**Findings:**
- **EF Core's reverse-engineering pipeline is relational-only.** `IDatabaseModelFactory` and `DatabaseModel`
  live in `Microsoft.EntityFrameworkCore.Relational`, and model tables, columns and foreign keys — there is no
  way to express a partition key or a clustering order. Scaffolding through `dotnet ef dbcontext scaffold` would
  therefore both reintroduce the dependency the provider avoids and produce a model missing the parts that
  decide whether a table can be queried. The scaffolder reads `system_schema` directly instead.
- A materialized view needs an `IS NOT NULL` guard on every key column: a view row can only exist when all of
  its key columns do.
- Views must be created after their base tables, so `EnsureCreatedAsync` orders the two passes.

## Phase 8 — Release

- [ ] Sample project exercising the provider end to end
- [ ] `FEATURES.md` — the supported and unsupported matrix
- [ ] `PER PARTITION LIMIT`, deferred from Phase 6
- [ ] 0.1.0
