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

## Phase 2 — Metadata, conventions, DDL, EnsureCreated

- [ ] Attributes and enums in the bare root namespace (`[PartitionKey]`, `[ClusteringKey]`, …)
- [ ] `HasPartitionKey` / `HasClusteringKey` / `ToTable` / keyspace replication builder extensions
- [ ] Conventions, including the primary key = partition keys ++ clustering keys rule
- [ ] `ScyllaModelValidator`
- [ ] `IScyllaDdlGenerator` + `ScyllaDatabaseCreator.EnsureCreatedAsync` / `EnsureDeletedAsync`
- [ ] Schema-agreement wait after DDL

## Phases 3–8

See the plan file. Order: type mapping → SaveChanges → query pipeline → Scylla-specific writes →
query ergonomics → scaffolding → docs and 0.1.0.
