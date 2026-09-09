# ADR-0001: Boundary-Written Timekeeping

**Status:** accepted for baseline  
**Date:** 2026-09-08

## Context

Legacy Grouper stores a session row, pause events, and accumulated pause seconds.
On stop it may replace that session with several segment rows. The UI reads
active sessions from SQLite every second to refresh labels. This creates needless
database traffic and leaves identity, concurrency, summary, and recovery bugs.

Elapsed display does not itself represent new user data. Start, pause, resume,
stop, correction, and recovery are the durable facts.

## Decision

Represent one tracking session as a stable aggregate with one or more active
interval rows.

- Start inserts session + open interval.
- Pause closes the interval.
- Resume inserts a new open interval.
- Stop closes an interval if needed and seals the session.
- Each command is one transaction with mutation log and idempotency receipt.
- No periodic elapsed value is persisted.
- In embedded mode, no periodic DB read occurs. UI advances from snapshot state
  using an in-memory monotonic clock.
- Daemon clients receive transition notifications and similarly render locally.
- Reports calculate interval overlap; they never split stored rows at midnight.

## Consequences

Positive:

- Database load is proportional to user actions, not elapsed time or open views.
- Session identity/audit survives pauses.
- Atomic expected-state transitions and idempotent retries are straightforward.
- Cross-midnight and DST reports can be exact without destructive maintenance.
- Sync replicates low-volume meaningful operations.

Costs:

- Active state and report queries join session/interval data.
- Crash/clock anomalies require explicit recovery UX.
- Clients need a shared active-session state coordinator and local UI clock.
- Concurrent intervals require clearly labeled sum versus wall-clock coverage.

## Rejected alternatives

- **Persist elapsed seconds on every tick:** unnecessary write amplification,
  contention, sync noise, and crash ambiguity.
- **Poll SQLite every second without writes:** still wastes connections/reads and
  couples presentation to persistence.
- **Split sessions into rows on pause/stop or midnight:** destroys aggregate
  identity and introduces boundary loss.
- **Full event sourcing for all product data:** adds projection/migration burden
  beyond present needs. A mutation log plus normalized canonical tables provides
  audit/sync readiness without making every query a replay.
