# Snook desktop and daemon operations

## Embedded desktop

The default desktop profile owns one SQLite workspace at:

`<platform local application data>/Snook/workspace.db`

Set `SNOOK_DATA_DIR` to relocate the profile before starting Snook. Do not copy
an open database. Create a verified backup first, stop the owning host, then
move the complete `Snook` directory and start with the new data-root setting.

The `.owner` lease beside the database is the ownership guard. A second
embedded host or daemon using the same workspace must stop with a store-lock
error before it can mutate data.

## Daemon profile

Start the daemon interactively for diagnostics:

```bash
SNOOK_DATA_DIR=/var/lib/snook snookd
```

The daemon binds only to loopback, writes its high-entropy token to
`Snook/daemon.token`, and prints a JSON readiness line. Clients must use the
token and must not open SQLite directly when `SNOOK_HOST_MODE=daemon` is set.
`Ctrl-C` requests graceful shutdown; the listener closes and the SQLite lease is
released.

The authenticated readiness endpoint is `/v1/health`. The change stream is
`/v1/changes`; clients reconnect and refresh their bootstrap snapshot before
resuming push notifications.

The headless CLI uses this same boundary. Configure it with `snook --host daemon --endpoint http://127.0.0.1:43871/ --token-file <data-root>/Snook/daemon.token doctor`, or the corresponding `SNOOK_*` environment variables. It fails closed
in daemon mode: a missing token, failed authentication, unavailable endpoint, or
protocol failure does not permit it to open the workspace database. This keeps
the CLI configuration compatible with a future server/daemon transport without
silently changing ownership mode.

## Backup and restore

Use Settings → Your data → Create backup, or the CLI/backend backup operation while the
owner is running. Backups use SQLite's online backup API and create a matching
`.manifest.json` containing byte count, SHA-256, and verification status.

Before restore, stop other clients that use the workspace. Restore replaces the
workspace only after the source is copied and `PRAGMA integrity_check` passes;
an interruption leaves the prior active database in place. Keep the manifest
with the backup when moving it between machines.

## Migration and recovery failures

Schema migration 8 (`daily-habits`) adds habits and their per-day check-ins in a
transaction under the existing ownership lease, with a separate checksum. It
does not alter tasks, calendar recurrence, or tracking sessions. Contract 1.4
adds habit reads and revision-checked writes to both hosts; the daemon dispatcher
uses the interface allowlist. Upgrade desktop, CLI, and daemon together.

Habit writes persist an exact, request-bound result receipt alongside the
aggregate revision and committed change. Exact retries, including after restart
or later edits, return that result without repeating the mutation or notification.
Archive/delete retain history; restore does not reinterpret dates. Habits store
their creation time zone, check-ins store civil dates plus UTC recording instants,
and corrections are limited to the last 366 days. The initial catalog limit is
500 habits including deleted records. JSON export schema 3 includes all habits
and check-ins under `habitTracking`; CSV remains a time-session report. Database
backups contain the habit tables and receipts. Restore upgrades a v7 backup in
its staging copy before replacing the workspace, so habit reads work immediately;
the original backup remains untouched.

Schema migration 7 (`task-due-dates-and-favorites`) indexes open date-only deadlines
and starred tasks/projects. Existing schemas already contain `tasks.due_date`
and favorite columns, so the migration preserves their values and all calendar
blocks instead of adding a duplicate due-date column or deriving deadlines from
schedule times. It runs transactionally under the ownership lease and has its own
checksum. As with other upgrades, keep a verified backup before upgrading.

Contract 1.3 adds `BulkUpdateTasksAsync` to both embedded and daemon clients.
Batches accept 1–500 distinct task IDs and expected revisions, use one SQLite
transaction, and create one `task-batch` committed change. The operation receipt
stores the affected tasks and their exact committed revisions for replay, even
after later edits or restart. A failed batch rolls back task fields, tag changes,
the receipt, and the change log. UI retries reuse the operation ID for an unchanged
request; explicitly loading latest task revisions creates a new attempt.

- A schema checksum or migration failure is reported as an incompatible-store
  error; Snook does not reset the database.
- Preserve the original database and its logs, then retry from a verified
  backup or a copy of the workspace.
- A crash/clock anomaly marks an active session `recovery-required`. Resolve it
  in Today using **Last known**, **Stop now**, or **Continue**; the decision and
  provenance remain durable.
- If the owner lease remains after an unclean process exit, confirm no Snook
  host is running before removing only the stale `.owner` file.

## Linux service

`deploy/systemd/snookd.service` is a hardened template for a packaged
`/usr/bin/snookd`. Install it with the package's service-management procedure,
then inspect `journalctl -u snookd` for the JSON readiness line. SIGTERM is
handled as a graceful daemon shutdown, so the listener closes and the workspace
lease is released before systemd's stop timeout. Keep the data directory private
to the service identity and back up before upgrades or migrations.
