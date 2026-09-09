# Legacy System Audit

## Method

The ZIP was extracted to an isolated temporary directory and inspected without
copying its implementation into the new source tree. Four independent code
audits covered product behavior, persistence/timekeeping, daemon/sync/security,
and migration/test quality. The five shipped screenshots and key docs, models,
repositories, migrations, UI controllers, protocol, runtime, and tests were
also reviewed directly.

Paths below are archive-relative, for example
`refs/grouper-main.zip!/grouper-main/grouper_core/models.py`.

## Legacy shape

| Surface | Evidence | Role |
|---|---|---|
| PySide6 desktop | `desktop/main.py`, `desktop/app.py`, `desktop/ui/**` | Primary UI |
| Core | `grouper_core/**` | Models, config, SQLite repositories, migrations |
| CLI | `cli/commands/**` | Session/task/activity/event/project/board queries and commands |
| Sync | `grouper_sync/**` | SQLite CDC, NDJSON/TCP peer replication, mDNS |
| Headless server | `server/**` | Sync runtime and read-only Flask pages |
| Installer | `installer/**`, `scripts/*.bat` | Windows-specific packaging |
| Tests | `tests/**` | 700+ unit, CLI, integration, and widget tests documented |

The archive metadata reports version `1.1.0.24` and Python 3.11+ in
`pyproject.toml:1-28`. Its package-boundary tests intentionally keep core,
desktop, sync, and server separated (`tests/unit/test_package_boundaries.py`).

## Product behavior worth preserving

### Planning

- Boards contain projects; projects contain tasks.
- Tasks have title, description, priority, due date, stars, tags, links,
  completion/deletion state, and task prerequisites.
- Unmet prerequisites block the dedicated completion command.
- Board and flat-list views support editing, completion, filtering, and moving
  a task between projects.
- Starred projects/tasks feed a reusable "Taskbox" on dashboard and agenda.

Evidence: `grouper_core/models.py:173-230,308-387`,
`grouper_core/database/tasks.py`, `desktop/ui/tasks/**`.

### Time tracking

- Activities are reusable time categories independent of projects.
- Sessions support start, pause, resume, stop, notes, optional task attribution,
  retroactive entry, and concurrent foreground activity.
- Background activities occupy a separate lane; legacy UI silently stops the
  current background session before starting another.
- Activity groups/tags organize a quick-start picker.

Evidence: `grouper_core/models.py:75-170,233-305`,
`grouper_core/database/sessions.py`, `desktop/ui/time/**`.

### Schedule and review

- Calendar has month, week, agenda, and timeline views.
- Tasks can be dragged into the agenda, which assigns a due time and creates or
  moves a linked event.
- Events support calendars, all-day state, recurrence, exceptions, task and
  activity links in storage, location, description, and color.
- Dashboard combines active sessions, Taskbox, upcoming tasks, two-day schedule,
  and a seven-day time strip.
- History separates completed tasks and sessions; Summary aggregates time,
  groups, daily trends, and completion metrics.

Evidence: `desktop/ui/calendar/**`, `desktop/ui/views/dashboard.py`,
`history.py`, `summary.py`, and the five images under `.github/assets/`.

### Ownership and automation

- Primary data is local SQLite, with backup/export concepts and relocatable
  paths in core code.
- CLI supports JSON output and major task/session workflows.
- Optional headless sync/web server and desktop-embedded sync exist.

These are strong product signals even where the old implementation is unsafe.

## What the timer actually does

The concern about per-second persistence is close but not exact:

- `desktop/ui/time/time_tracker.py:79-81` starts a one-second UI timer.
- `_update_timers()` at `:334-372` calls `get_active_sessions()` each tick,
  creating a SQLite connection and reading active rows.
- Dashboard has another one-second tick path and reads active sessions
  (`desktop/ui/views/dashboard.py:341-343,513,583`).
- Durable session writes occur at lifecycle boundaries in
  `grouper_core/database/sessions.py`: start `:75-88`, stop `:91-167`, pause
  `:187-199`, and resume `:202-229`.

Therefore, the old client is **not writing duration every second**, but it is
repeatedly reopening and reading SQLite to render clocks. Snook removes
both periodic writes and single-client periodic reads. It loads an active-state
snapshot and advances labels with an in-memory monotonic clock; daemon clients
receive transition notifications and use occasional reconnect reconciliation,
not one query per second.

## Material data and correctness defects

1. **Mutable natural identity.** `sessions.activity_name` has no activity FK
   (`connection.py:320-337`); activity rename rewrites history and deletion can
   orphan it.
2. **Destructive pause materialization.** Stop creates replacement session rows
   for active segments and deletes the original and pause events
   (`sessions.py:136-165`), losing aggregate identity/audit history.
3. **Cross-midnight loss.** Splits end at `23:59:59` and restart at midnight,
   losing one second per boundary (`sessions.py:399-435`).
4. **Incorrect day attribution.** Summaries group all duration under
   `date(start_time)` rather than interval overlap (`sessions.py:373-391`).
5. **Mixed naive local time.** Python `datetime.now()`, `CURRENT_TIMESTAMP`, and
   SQLite `datetime('now','localtime')` are mixed across schema and commands.
6. **Weak lifecycle concurrency.** Read-then-write pause/stop flows lack an
   atomic expected-state claim, busy policy, and affected-row validation.
7. **Underconstrained schema.** Session state, interval ordering, priority,
   recurrence, and mutually dependent fields are mostly app-enforced.
8. **Broken migration order.** `init_database()` applies current schema/index
   DDL before old-schema migrations (`connection.py:576-607`); a reproduced old
   `projects(id,name)` schema fails while creating an index on missing `uuid`.
9. **Unsafe live backup.** `shutil.copy2()` copies SQLite without its online
   backup API (`connection.py:128-143`).
10. **Duplicated cross-aggregate rules.** Task due dates and linked events call
    each other's repositories after separate commits (`tasks.py:258-320`,
    `events.py:86-159`), permitting partial updates.
11. **Completion bypass.** Generic task update can set `is_completed` without
    prerequisite checks or maintaining `completed_at` (`tasks.py:258-288`).
12. **Recurrence end ignored.** `recurrence_end_dt` is stored but not applied by
    range expansion (`events.py:162-242`).
13. **Inconsistent deletion.** Activities/tasks use mixed archive/soft-delete
    behavior while events and sessions can be hard-deleted.

## Architecture and security findings

- The core/sync/server direction is useful, but the desktop still knows concrete
  DB functions and owns ad hoc polling/refresh behavior.
- Sync uses raw NDJSON/TCP (`grouper_sync/protocol.py:1-121`) and self-asserted
  device IDs. It has versions, tombstones, deferred FKs, and deterministic
  convergence machinery, but no authentication, authorization, or encryption.
- The sync server defaults to `0.0.0.0` (`server/runtime/runner.py:24-35`) and
  mDNS advertises device information.
- Read-only Flask pages have no authentication if bound beyond loopback.
- Conflict rows exist without a complete user resolution workflow.
- Packaging, installer, registry, custom window code, and batch scripts are
  Windows-specific. Android has no legacy implementation to port.
- README privacy claims conflict with optional network transmission.

## Documentation/UI contradictions

- README claims history search/filter and "every" session, but the view loads
  fixed 50-item lists.
- README promises moving the data directory; Settings only displays a path and
  offers backup.
- System Tasks/Tracked Sessions calendars are UI overlays rather than ordinary
  event streams.
- Event storage links tasks, but the event editor exposes only activity links.
- A session model docstring says sessions belong to projects while storage and
  UI attach them to activities.
- The current project path default differs between config and connection code.

## Test evidence and gaps

One independent audit used the archive's offline environment and reported:

- 312 core/database tests passed.
- 90 sync/integration tests passed.
- 139 CLI/server/boundary tests passed.
- A widget batch reported 37 passing and one dashboard layout failure.
- Ruff passed for reviewed packages.

Other isolated reviewers could not invoke `pytest` directly, so these are audit
observations rather than this repository's CI result. The test suite offers
valuable behavioral evidence, but important gaps remain: authentic historical
DB fixtures, migration interruption, contention, hostile protocol input,
multi-peer convergence properties, Windows packaging, Linux packaging, and all
Android behavior.

## Porting rule

Preserve user intent and tested workflows, not incidental tables or UI widgets.
No legacy repository, migration, sync trigger, installer path, or timer loop is
accepted as a design constraint unless this specification says so explicitly.
