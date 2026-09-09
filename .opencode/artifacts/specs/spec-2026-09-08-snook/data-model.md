# Data and Persistence Model

## Persistence decision

Use SQLite as the authoritative local store through `Microsoft.Data.Sqlite`,
with explicit SQL and repository/query objects behind application ports. SQLite
fits a single-user, local-first product and is available on all target
platforms. The redesign addresses misuse of SQLite rather than replacing it
with a server database.

The schema below is logical. Exact DDL is produced and reviewed in milestone 1,
including every `CHECK`, FK action, partial index, and migration.

## Identity and common columns

- Durable aggregate and operation IDs are UUIDv7 generated in the application.
- SQLite stores UUIDs as 16-byte BLOBs; contracts expose canonical strings.
- Mutable names are never foreign keys.
- Mutable aggregates carry `revision INTEGER NOT NULL` for optimistic commands.
- Syncable records carry `created_at_utc_ms`, `updated_at_utc_ms`, and optional
  `deleted_at_utc_ms`; timestamps do not replace revisions.
- User content is soft-deleted unless a retention/purge operation says otherwise.
- Junction rows have their own stable IDs when they must sync independently.

## Logical tables

### Planning

```text
workspaces(id, name, created_at, revision)
boards(id, workspace_id, name, sort_key, archived_at, deleted_at, revision)
projects(id, board_id, name, description, default_activity_id?, starred,
         sort_key, archived_at, deleted_at, revision)
tasks(id, project_id, title, description, priority, status,
      due_kind, due_local_date?, due_at_utc_ms?, due_time_zone?,
      default_activity_id?, starred, sort_key,
      completed_at_utc_ms?, archived_at_utc_ms?, deleted_at_utc_ms?, revision)
task_dependencies(id, task_id, prerequisite_task_id, created_at, revision)
task_links(id, task_id, label?, uri, kind, created_at, deleted_at?, revision)
```

Rules:

- Task priority is a constrained value, not silently clamped.
- `status` initially supports open/completed; archive/deletion are orthogonal.
- Date-only and instant deadlines are distinct and mutually constrained.
- Dependency edges reject self-reference in DDL and cycles in the same write
  transaction.
- Board/project name uniqueness is scoped and case-normalized deliberately;
  global name uniqueness is not required.

### Effort taxonomy and tags

```text
activities(id, workspace_id, name, description, lane_default,
           archived_at, deleted_at, revision)
activity_groups(id, workspace_id, name, sort_key, deleted_at, revision)
activity_group_memberships(id, activity_id, group_id, sort_key, revision)
tags(id, workspace_id, normalized_name, display_name, color?, deleted_at, revision)
project_tags(id, project_id, tag_id, revision)
task_tags(id, task_id, tag_id, revision)
activity_tags(id, activity_id, tag_id, revision)
```

No arbitrary three-group limit is encoded. Separate FK-backed tag joins are
preferred over a polymorphic `entity_type/entity_id` relation because SQLite can
then enforce referential integrity.

### Time tracking

```text
tracking_sessions(
  id, workspace_id,
  task_id?, activity_id?,
  lane, state,
  started_at_utc_ms, stopped_at_utc_ms?,
  notes, source, recovery_status?,
  created_at_utc_ms, updated_at_utc_ms, deleted_at_utc_ms?, revision
)

active_intervals(
  id, session_id,
  started_at_utc_ms, ended_at_utc_ms?,
  source, created_at_utc_ms, revision
)

time_corrections(
  id, session_id, operation_id,
  reason?, before_json, after_json,
  corrected_at_utc_ms, corrected_by_device_id
)
```

Required constraints:

- Session has `task_id IS NOT NULL OR activity_id IS NOT NULL`.
- State is running, paused, stopped, or recovery-required.
- `stopped_at` is null only for non-stopped states and is not before start.
- Interval end is null or greater than/equal to start.
- A partial unique index allows at most one open interval per session.
- Running requires one open interval; paused/stopped requires none. Because this
  spans tables, each lifecycle command validates it in its transaction and a
  startup integrity scanner verifies it.
- Normal user commands cannot add an interval to a stopped session.
- Soft-deleting task/activity never deletes historical sessions; FK action is
  RESTRICT or references remain to soft-deleted rows.

The database does not store an incrementing “elapsed seconds” field. Closed
duration is `ended - started`. Open duration is calculated from the current
clock plus the interval start. UI animation may use a monotonic process clock,
but durable boundaries are UTC instants.

### Calendar

```text
calendars(id, workspace_id, name, color, visible, weekly_budget_minutes?,
          archived_at, deleted_at, revision)
schedule_blocks(id, calendar_id, task_id?, activity_id?, title_override?,
                start_utc_ms, end_utc_ms, time_zone,
                recurrence_rule?, recurrence_end?, deleted_at?, revision)
calendar_events(id, calendar_id, title, description, location,
                temporal_kind, start fields, end fields, time_zone?,
                recurrence_rule?, recurrence_end?, color?, deleted_at?, revision)
recurrence_exceptions(id, series_kind, series_id, occurrence_key,
                      action, replacement_payload?, revision)
```

Task deadlines and sessions are query projections, not copied calendar rows.
Recurring records retain local civil-time fields and an IANA zone where needed
so “9 AM every Monday” remains 9 AM across DST. Expansion has a caller-supplied
range and maximum occurrence count. The DDL design review must decide whether
schedule blocks and events share one internal temporal table; contracts keep
their semantics distinct either way.

### Settings, host state, and replication

```text
workspace_settings(key, value_json, revision)        -- safe to sync
schema_migrations(sequence, name, checksum, applied_at, app_version)
operation_receipts(operation_id PRIMARY KEY, status, result_code, aggregate_id?,
                   aggregate_revision?, committed_cursor?, result_payload?,
                   error_code?, applied_at)
mutation_log(sequence, operation_id, device_id, aggregate_type, aggregate_id,
             base_revision, new_revision, kind, payload, occurred_at)
sync_tombstones(aggregate_type, aggregate_id, revision, deleted_at)
sync_conflicts(id, aggregate_type, aggregate_id, local_payload,
               remote_payload, status, created_at, resolved_at?)
```

Host/UI settings, data path, tokens, keys, and device credentials live outside
workspace settings in platform-private configuration/secret stores. A local
mutation log is written in the same transaction as each syncable mutation; it
supports UI change cursors and future sync without making sync mandatory.

## Lifecycle transactions

All commands include `operation_id`, expected aggregate revision where
applicable, actor/device ID, and a captured UTC instant from `TimeProvider`.

### Start

1. Validate task/activity exists and is not deleted.
2. Apply configured lane concurrency policy.
3. Insert session in running state.
4. Insert one open interval.
5. Insert mutation-log entry and operation receipt.
6. Commit once, then publish change notification.

### Pause

1. Load session in the write transaction and match expected revision/state.
2. Close its open interval at command instant.
3. Set paused state and increment revision.
4. Append mutation/receipt; commit once.

### Resume

1. Match expected paused state/revision and lane policy.
2. Insert an open interval at command instant.
3. Set running state; append mutation/receipt; commit once.

### Stop

1. Match running or paused state/revision.
2. If running, close open interval.
3. Set stopped state/time and optional notes/attribution edits.
4. Append mutation/receipt; commit once.

Expected-state predicates and affected-row checks prevent two clients from
stopping or pausing the same revision successfully. Replayed operation IDs
return the stored logical result.

## Time semantics

- Instants are Unix epoch milliseconds in UTC.
- Durations use integer milliseconds; UI may round for display only.
- Profile/report time zone is a canonical IANA identifier. Host-zone changes do
  not reinterpret historical instants.
- Date-only deadlines stay date-only and are never converted through midnight
  UTC.
- Recurrences distinguish zoned local time, floating local time, date-only, and
  absolute instants.
- Clock rollback that would close an interval before it starts is rejected into
  recovery-required state, never clamped silently.
- For normal intervals, elapsed duration is end minus start. A future
  monotonic-duration enhancement must specify allocation semantics before adding
  a second authoritative duration field.

### Reporting by civil day

Stored sessions are never physically split at midnight. For each requested
local day, convert that day's start/end in the report zone to UTC and calculate:

```text
overlap = max(0, min(interval_end, day_end) - max(interval_start, day_start))
```

This naturally handles 23/24/25-hour DST days. Open intervals use one captured
query instant so all rows in a report agree.

### Concurrent time

- Attributed duration is the sum of matching interval overlaps.
- Wall-clock coverage is the union of matching overlaps.
- Global dashboards label which measure is shown.
- A session linked to both task and activity counts once globally, once in the
  task dimension, and once in the activity dimension.

## SQLite operating profile

The writable store is configured once under exclusive startup ownership,
initially:

```sql
PRAGMA foreign_keys = ON;
PRAGMA journal_mode = WAL;
PRAGMA synchronous = FULL;
PRAGMA busy_timeout = 5000;
PRAGMA trusted_schema = OFF;
```

Exact mobile tradeoffs are benchmarked before changing durability. Since timer
writes are sparse, `FULL` is the safe initial policy. Writer connections verify
foreign keys, synchronous mode, and busy timeout. Reader connections enable
foreign keys, busy timeout, and `query_only` but do not attempt to change journal
mode. The database is supported only on
local/app-private filesystems; SMB/NFS/network shares are not live-store targets.

Within a backend host:

- One bounded async writer queue serializes short write transactions.
- Read-only query connections may run concurrently against WAL snapshots.
- No repository calls `Commit` independently inside a larger use case.
- Cancellation before commit rolls back; after commit the operation receipt
  makes retries safe.
- Busy/corrupt/full/permission errors are typed and surfaced.

Across processes, an OS-held store lease prevents embedded/daemon double
ownership. The lease is advisory defense in depth; no supported client bypasses
the backend host to open the DB.

## Index baseline

At minimum, DDL/query-plan tests cover:

- projects by board, archive state, sort key;
- tasks by project/status/sort, deadline, star, updated cursor;
- dependencies in both directions;
- sessions by task, activity, state, start/stop, deleted state;
- intervals by session/start and by range overlap;
- partial unique open interval per session;
- calendar records by calendar/range;
- normalized tag/group/activity names within workspace;
- mutation log by sequence/device/aggregate and tombstone retention.

Overlap queries may use `started_at < range_end AND
(ended_at IS NULL OR ended_at > range_start)`. Benchmarks, not intuition,
determine additional indexes/materialized daily summaries. No aggregate cache is
authoritative.

## Migrations, backup, and recovery

- Current DDL is not run against old tables before migrations.
- Migration scripts are embedded resources with sequence/name/checksum.
- Startup acquires exclusive ownership, takes a SQLite online backup, runs
  pending migrations, executes `foreign_key_check` and `quick_check`, then
  commits/activates the new version.
- Non-transactional rebuild steps use a documented shadow-table protocol and
  fault-injection tests.
- Downgrade does not mutate a newer DB; it fails with a clear compatibility
  error.
- Backup manifests include schema/app version, created time, DB hash, page
  settings, and optional attachment/export inventory.
- Restore happens beside the active DB, validates, then atomically switches
  under exclusive ownership. The old DB is retained until explicit cleanup.

## Data access rule

Avalonia views/view-models, CLI handlers, RPC services, sync transports, and
export UI never execute SQL. They invoke application commands/queries. SQLite
adapters return domain/application models rather than exposing connection or row
objects.
