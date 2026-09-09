# ADR-0003: SQLite with One Logical Writer

**Status:** accepted for baseline  
**Date:** 2026-09-08

## Context

SQLite is portable, inspectable, backup-friendly, and sufficient for a personal
local-first workspace. Legacy pain came from denormalized identity, many
short-lived connections, scattered commits, weak migration order, underconstrained
state, and multiple surfaces touching persistence—not from a demonstrated need
for PostgreSQL or another server database.

Embedded and daemon modes create a process-ownership risk, while simultaneous
UI/CLI/network commands create a write-order risk.

## Decision

- Keep SQLite as authoritative local storage via `Microsoft.Data.Sqlite`.
- Only the selected backend host opens writable persistence.
- Acquire an OS-held per-store lease before migration/write readiness.
- Serialize writes through one bounded logical writer inside that host.
- Use short explicit transactions and concurrent read-only WAL snapshots.
- Begin with foreign keys ON, WAL, synchronous FULL, busy timeout, and trusted
  schema OFF where supported.
- Use explicit SQL/migrations and verified backup/restore rather than hiding
  database behavior behind UI models.
- Do not support a live DB on network filesystems.

## Consequences

Positive:

- Deterministic lifecycle ordering and fewer lock races.
- Low operational burden and full offline support across target platforms.
- Safe online backup and portable export remain practical.
- Query behavior and constraints can be tested directly.

Costs:

- Long writes must be decomposed/staged so the queue remains responsive.
- Multi-user centralized scale is out of scope.
- OS store leases need platform implementations and tests.
- Android native SQLite packaging/ABI behavior must be release-tested.
- SQLCipher, if later selected, requires separate native/provider work.

## Rejected alternatives

- **Database server for all modes:** adds accounts/network/service operation and
  violates the lightweight local-first default.
- **One writable connection per UI component:** repeats legacy transaction and
  polling coupling.
- **Rely on SQLite busy retries alone:** does not define application command
  ordering or prevent embedded/daemon dual ownership.
- **Generic ORM-first model:** may be revisited for projections, but canonical
  lifecycle DDL/transactions need visible SQL and affected-row control.
