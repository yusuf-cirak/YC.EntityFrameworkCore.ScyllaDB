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

## Phase 4 — Query pipeline

- [ ] `CqlSelectExpression` with typed restriction buckets, not a boolean predicate tree
- [ ] `ScyllaQueryableMethodTranslatingExpressionVisitor` — Where/Select/OrderBy/Take/First/Count/Any
- [ ] `CqlQueryValidator` — partition key, contiguous clustering prefix, ordering rules
- [ ] `ScyllaShapedQueryCompilingExpressionVisitor` + materialization
- [ ] Every unsupported construct throws at translation time with its documented message

## Phases 5–8

See the plan file. Order: Scylla-specific writes (TTL, `USING TIMESTAMP`, LWT concurrency) → query ergonomics
(ALLOW FILTERING, paging, indexes, materialized views) → scaffolding → docs and 0.1.0.
