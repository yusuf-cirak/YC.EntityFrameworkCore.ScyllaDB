# Contributing

Thanks for taking an interest. This is an Entity Framework Core database provider, which means most of the code
lives against EF Core's internal ("pubternal") APIs. That is expected here — `EF1001` is suppressed
repo-wide — but it also means changes need care.

## Getting set up

```bash
dotnet build YC.EntityFrameworkCore.ScyllaDB.slnx
dotnet test test/YC.EntityFrameworkCore.ScyllaDB.UnitTests
dotnet test test/YC.EntityFrameworkCore.ScyllaDB.FunctionalTests   # needs Docker
```

The SDK version is pinned in `global.json`. Package versions are managed centrally in
`Directory.Packages.props` — add the version there, and reference the package without a version in the csproj.

## Code style

There is no `.editorconfig` and no StyleCop. Match the surrounding code:

- File-scoped namespaces, `Nullable` enabled, 4 spaces, Allman braces.
- `public virtual` on members of non-static public classes, so consumers can subclass and `Replace` services.
- Extension methods go in EF Core's own namespaces (`Microsoft.EntityFrameworkCore`,
  `Microsoft.Extensions.DependencyInjection`) with `// ReSharper disable once CheckNamespace`.
- Attributes and enums live in the bare root namespace, so one `using` gets a user everything they need.
- Fluent verbs: `Is*` for a mapping kind, `Has*` for adding an object, `With*` for a behavior toggle.
- Guard clauses use the modern helpers: `ArgumentNullException.ThrowIfNull`,
  `ArgumentException.ThrowIfNullOrWhiteSpace`.
- String comparisons always pass `StringComparison.Ordinal` explicitly.
- Comments explain *why*, not *what*. If a decision is non-obvious, leave a sentence.

Warnings are errors. XML docs are generated, but `CS1591`/`CS1573` are suppressed, so undocumented members will
not fail the build — document public API anyway.

## Error messages are a feature

CQL cannot express most of what LINQ can. Every unsupported construct must fail at **translation** time with a
message that names the construct, says why it cannot work, and gives the alternative. A bare
"could not be translated" is a bug. Every message lives in `ScyllaStrings` and has a test asserting its exact
text.

## Tests

- **Unit tests** never touch a network. They assert generated CQL strings, model validation errors, and write
  batching, using a recording fake for the client wrapper.
- **Functional tests** run against a real ScyllaDB node via Testcontainers, one container per test assembly, one
  keyspace per test class.

New behavior needs a test in whichever suite can prove it without a database, plus a functional test if it
touches the wire.

### On `Microsoft.EntityFrameworkCore.Specification.Tests`

We take the package for its utilities (`TestStore`, `ListLoggerFactory`, `TestHelpers`) and wire the
semantics-agnostic bases — model building, type mapping, value conversion. We deliberately do **not** wire the
query-shaped bases: they assume joins, `Include`, `Skip`, and arbitrary `WHERE`, none of which CQL has, so they
would produce a wall of skips and find nothing. This has been decided; please do not reopen it in a PR review.

## Commits and releases

Releases are automated with [Release Please](https://github.com/googleapis/release-please). PRs are squash
merged, and **the PR title becomes the commit message**, so it must follow
[Conventional Commits](https://www.conventionalcommits.org/):

```
feat: add USING TTL support to SaveChangesAsync
fix: quote reserved words in generated CQL identifiers
docs: document the clustering prefix rule
```

`feat:` bumps the minor version, `fix:` the patch, and `feat!:` or a `BREAKING CHANGE:` footer the major.

## Good first contributions

- The package icon (`PackageIcon`) is not set yet.
- Error messages that could be clearer or more actionable.
