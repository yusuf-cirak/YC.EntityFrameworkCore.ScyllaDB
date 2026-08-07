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

## Phase 1 — Options, session, DI, async guards

- [ ] `ScyllaOptionsExtension` + `ExtensionInfo` (service-provider hash covers every connection-shaping value)
- [ ] `ScyllaDbContextOptionsBuilder` and the four `UseScyllaDb` overloads
- [ ] `ScyllaSingletonOptions` / `IScyllaSingletonOptions`
- [ ] `ScyllaSingletonClientWrapper` (singleton `ICluster` + `ISession`, prepared-statement cache)
- [ ] `ScyllaClientWrapper` (scoped facade)
- [ ] `ScyllaTransactionManager` — every member throws
- [ ] `ScyllaThrow` / `ScyllaStrings`
- [ ] `AddEntityFrameworkScyllaDb` — full service graph, stubs throwing `NotImplementedException`
- [ ] Functional: container fixture + `CanConnectAsync` against a real node

## Phases 2–8

See the plan file. Order: metadata/DDL/EnsureCreated → type mapping → SaveChanges → query pipeline →
Scylla-specific writes → query ergonomics → scaffolding → docs and 0.1.0.
