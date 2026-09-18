# Daily habits: initial scope

Implemented and verified. See [progress.md](progress.md) for the final scope and
verification evidence, and [cli.md](cli.md#daily-habit-reads) for query semantics.

Implement a separate, workspace-wide daily yes/no habit tracker. Habits do not
create tasks, calendar events, reminders, or tracked time. Numeric targets,
selected weekdays, and automatic timer-based completion are outside this slice.

1. Add immutable habit definitions and civil-date check-ins. A habit keeps its
   creation time zone and start date, so travel, DST, and daemon host settings do
   not shift recorded days. Allow a start date and corrections within the last
   366 days. Today may remain unfinished without breaking yesterday's streak.
2. Add an additive SQLite migration, aggregate revisions, atomic explicit
   completion/undo, durable request-bound receipts, and committed notifications.
   Preserve history across archive/delete/restore, backup, and JSON export.
3. Extend the shared contract, embedded implementation, and daemon client. The
   daemon's contract-derived dispatcher exposes the same methods. Bound reads to
   1–366 days and the initial workspace catalog to 500 habits including deleted.
4. Add a Habits workspace with direct check-ins, a recent-day history, progress,
   creation/editing in the shared draft-preserving drawer, and lifecycle actions.
   Include keyboard names, focus containment, and stale-edit recovery.
5. Add a read-only `habits` CLI helper, retaining the existing generic contract
   invocation surface for automation. Document civil dates and mutation rules.
6. Verify domain day rules and embedded/daemon parity, replay after restart,
   migration, backup/export, CLI JSON, and persisted GUI interactions. Render and
   inspect seeded/empty and 980×640 screens, then run the full repository checks.
