# Security Policy

## Supported versions

Only the latest released version receives security fixes.

## Reporting a vulnerability

Please report security issues privately through
[GitHub Security Advisories](https://github.com/yusuf-cirak/YC.EntityFrameworkCore.ScyllaDB/security/advisories/new)
rather than opening a public issue.

Expect an acknowledgement within a few days. Once a fix is ready it ships in a patch release and the advisory is
published.

## Scope notes

Two provider-specific things are worth calling out:

- **Paging tokens are not encrypted.** `ScyllaPage<T>.ContinuationToken` wraps the driver's raw paging state.
  A tampered token can be used to read rows from other partitions. Never hand one to an untrusted client without
  signing it, and never accept one back unverified.
- **`ALLOW FILTERING` is a denial-of-service vector.** A query that opts into it scans the cluster. Do not let
  untrusted input decide whether it is applied.
