# Snook headless CLI

`snook-cli` is the headless, structured-JSON frontend for Snook's public backend
contract. It is designed for shell scripts and agents: successful values are
JSON on standard output, errors are JSON on standard error, and it does not
render human-oriented tables that need reparsing.

## Running it

The Fedora RPM installs `snook-cli` alongside `snook` (the GUI) and `snookd`
(the daemon). The raw CLI publish output is named `snook`; the package wrapper
provides the unambiguous `snook-cli` command used throughout this reference.

During development, invoke the project directly:

```bash
dotnet run --project src/Snook.Cli/Snook.Cli.csproj -- [global options] command
```

Global options precede the command.

| Option | Environment fallback | Meaning |
| --- | --- | --- |
| `--data-dir PATH` | `SNOOK_DATA_DIR` | Root containing the `Snook` data directory. |
| `--host embedded\|daemon` | `SNOOK_HOST_MODE` | Select SQLite ownership or remote daemon transport. |
| `--endpoint URL` | `SNOOK_DAEMON_ENDPOINT` | HTTP loopback endpoint with no path, query or credentials. |
| `--token TOKEN` | `SNOOK_DAEMON_TOKEN` | Daemon authentication token. Prefer a token file. |
| `--token-file PATH` | `SNOOK_DAEMON_TOKEN_FILE` | Private file containing the daemon token. |
| `--no-profile` | — | Explicitly ignore saved settings; requires `--host` or `SNOOK_HOST_MODE`. |

Clients read the shared version-1 `<launch-data-dir>/Snook/client-profile.json`.
The launch directory is selected from `--data-dir`, `SNOOK_DATA_DIR`, or the
platform local-application-data default before reading the saved profile. Each
explicit flag overrides the matching environment value, then the saved value.
GUI **Connection…** / `snook --configure` manages the profile. It contains only
host mode, data directory, endpoint and token-file path, never the token itself.
`SNOOK_DAEMON_PORT`, when present without an explicit endpoint, overrides a saved
endpoint; blank endpoint/token-file overrides restore discovery/default-file use.
Malformed, unsupported, oversized, nonprivate or linked profiles fail before a
workspace is opened, rather than reverting to embedded. Repair/move the bad file
or explicitly use `--no-profile --host daemon` with the required connection options.

Without overrides or a saved profile, embedded mode is the default and opens `<data-dir>/Snook/workspace.db`.
Daemon mode uses the endpoint and token above; when no token is supplied it
reads `<data-dir>/Snook/daemon.token`. It never opens SQLite, including after an
authentication or connection failure. A daemon currently means the authenticated
loopback `snookd` host, but this boundary is intentionally the configuration
point for a later server implementation.

An explicitly selected token file takes precedence over `SNOOK_DAEMON_TOKEN`.
The desktop supports the same host/data-directory/endpoint/token-file options.
Use `--host daemon`, export `SNOOK_HOST_MODE=daemon`, or save a daemon profile while
the daemon owns the workspace. `doctor` reports the resolved endpoint (including
discovered/custom ports) and checks the
connection and negotiated contract, and `watch` follows committed changes.
Invalid port settings fail explicitly. Clients bypass HTTP proxies and refuse
redirects so the authentication token stays on the selected loopback endpoint.

Exit codes are `0` for success, `2` for a domain or validation error, `64` for
CLI syntax/argument errors, `1` for an unexpected host error, and
`130` when a caller cancels the command.
Daemon availability, authentication and compatibility errors use exit `2` with
`StoreUnavailable`. A timed-out write may have committed: retry the unchanged
request with its original operation ID. Contract 1.6 adds optional caller requests
to the remaining entity create/add methods. Invocations that omit the optional
request still start a new operation each time: inspect saved data before repeating
one after an uncertain response. The CLI never automatically retries a write.

With schema 10, commands that accept an operation request return the exact saved
result on an unchanged retry, including after later edits or restart. Preserve
the full payload and expected revision; a changed payload needs a new operation
ID after reviewing the current state. Results from a retry describe the original
commit, so read current data separately when needed. An older receipt without
enough information to verify the original request fails explicitly instead of
returning a later record or repeating the write. Database backups preserve this
retry history; JSON/CSV exports do not.

## Daily habit reads

`habits` is a read-only helper over `GetHabitsAsync`. With no arguments it returns
active habits and their last 30 civil days, each ending today in that habit's
saved time zone. Supply one optional JSON query:

```bash
snook-cli habits
snook-cli habits '{"days":7}'
snook-cli habits '{"days":30,"throughDate":"2026-09-15","includeArchived":true,"includeDeleted":true}'
```

Results contain `habit` (including its revision, fixed start date, and time zone),
`today`, `rangeStart`, `rangeEnd`, `checkIns` with civil dates and UTC recording
timestamps, `completedDays`, `eligibleDays`, and `currentStreak`. The range is
inclusive and bounded to 1–366 days. Eligible days exclude dates before the habit
started and after its today; the completion count is for this range. The streak
is always current and may extend beyond the read window. Pending today preserves
yesterday's streak. Archived/deleted habits keep history but cannot be checked
off until restored. Revealing deleted habits also reveals deleted archived habits.

The generic `call` surface also exposes `create-habit`, `update-habit`,
`set-habit-completion`, `set-habit-archived`, and `set-habit-deleted` for automation.
Use `api` for their exact arguments. Creation takes `definition` with `name`,
`description`, `startDate`, and `timeZone`, plus a `request` with operation/device
IDs and no expected revision. Other writes require the habit's expected revision.
`set-habit-completion` takes an explicit `day` and `completed` boolean; sending
`false` undoes a check-in. Reuse the exact request and operation ID to retry a
write. Reusing an operation ID with changed arguments is rejected. All writes
use the same embedded/daemon contract as the GUI.

## Discovering the contract

```bash
snook-cli api > snook-api.json
snook-cli doctor
snook-cli bootstrap
```

`api` is the authoritative machine-readable catalog. It includes every callable
`IBackendClient` method, its exact named arguments, defaults, and return type.
It is generated from the interface, so additions to the desktop/backend contract
automatically appear in the CLI. `doctor` verifies the selected host and reports
the workspace and advertised capabilities. `bootstrap` returns the complete
initial workspace projection: hierarchy, settings, today state, calendars,
deleted items, capabilities, and committed cursor.

`watch` is the streaming counterpart for the `IBackendClient.Changed` event. It
emits newline-delimited JSON (NDJSON), with one complete JSON object per physical
line: a `{ "type": "ready" }` record with the initial cursor and
`{ "type": "change" }` records for commits or reconnect invalidations. The
subscription starts before bootstrap, so a change/reconnected record can precede
the ready record; consumers must inspect `type` rather than assume the first
line is ready. It runs until the caller cancels it (normally Ctrl-C) and is the
appropriate daemon-mode replacement for polling SQLite.

Convenience read commands are available for common automation:

```bash
snook-cli tasks [search]
snook-cli summary day|task|project|activity|activitygroup|tag|lane [days]
snook-cli calendar blocks|events '{"rangeStartUtc":"2026-09-13T00:00:00Z","rangeEndUtc":"2026-09-20T00:00:00Z"}'
```

`history` optionally accepts a `HistoryQuery` JSON object, for example:

```bash
snook-cli history '{"rangeStartUtc":"2026-09-01T00:00:00Z","rangeEndUtc":"2026-10-01T00:00:00Z","search":"planning","pageSize":100}'
```

`calendar` defaults to the next seven days when the range object is omitted.

## Planning and applying task batches

Use the dedicated two-step command for mass edits. `plan` resolves a selection to
exact task IDs and revisions without writing anything, then returns a JSON document
that includes a readable preview, the patch, and stable operation/device IDs.
Review and retain that document; `apply` accepts it unchanged and commits the whole
batch atomically.

```bash
snook-cli task-batch plan '{
  "selection":{"projectId":"<project-id>","search":"release","status":"Open"},
  "update":{
    "priority":"Urgent",
    "changeDueDate":true,
    "dueDate":"2026-09-20",
    "starred":true,
    "tagsToAdd":["release"]
  }
}' > /tmp/snook-release-plan.json

snook-cli task-batch apply "$(cat /tmp/snook-release-plan.json)"
```

Selections are intersections of the supplied fields. They support `taskIds`,
`search`, `boardId`, `projectId`, `status`, `priority`, `starred`, `hasDueDate`,
`dueOnOrAfter`, and `dueOnOrBefore`. Open, unarchived tasks are the default;
`includeCompleted` and `includeArchived` opt into those states. Deleted tasks and
tasks under inactive projects or boards are never selected. To intentionally
select every eligible task, use `{"all":true}`; an empty selection is rejected.
Explicit task IDs must all exist and match every other supplied filter, so a typo or
stale filter cannot silently shrink the batch. Plans contain 1–500 tasks.

The `update` object is the same selective patch used by the desktop bulk editor:
title, description, priority, status, project, favorite and archive state, plus tag
additions/removals. Set `changeDueDate` to `true` to set `dueDate`, or pair it with
`"dueDate":null` to clear the date. `changeActivity` has the same set/clear behavior
for `defaultActivityId`. Omitted fields remain unchanged on every selected task.

If any target revision is stale or any resulting task violates dependency or
validation rules, no task changes. Generate a new plan to review current revisions.
After a successful or response-uncertain apply, retry the exact saved plan rather
than planning again: its operation ID makes the retry return the original committed
result without applying the patch twice. The result identifies the operation and
contains every updated task. This workflow behaves identically with `--host
embedded` and `--host daemon`.

## Calling every capability

The general form is:

```bash
snook-cli call METHOD '{"namedArgument":"value"}'
```

For example, these command spellings identify the same method:
`create-task`, `CreateTask`, and `CreateTaskAsync`. Enumerations are string
names (`"High"`, `"Foreground"`, `"Earlier"`, `"StopNow"`). GUIDs, dates, and
timestamps are standard JSON strings; timestamps should be explicit UTC ISO 8601
instants. Use `null` for nullable values, never an empty UUID placeholder.

For retryable creation, generate operation/device UUIDs before the first send and
keep the entire request JSON. Send the identical request again to confirm an
uncertain result; do not generate a fresh operation ID for that retry:

```bash
snook-cli --host daemon call create-task '{
  "projectId":"<project-id>",
  "title":"Draft launch note",
  "request":{"operationId":"<same-guid-on-retry>","clientDeviceId":"<stable-client-guid>"}
}'
```

The optional `request` is available on create-board/project/activity/activity-group/
calendar/calendar-event/schedule-block/task and add-task-link/task-tag/project-tag/
activity-tag/task-dependency. Creates require nonempty operation/device IDs and
no `expectedRevision` (omit it or use `null`). Changed command arguments under the
same operation ID are rejected. Journals, journal entries and habits already
require a request; manual sessions already support one. Maintenance operations
such as backup/export/restore are separate from entity creation receipts.

The short creation examples below omit `request` for readability and therefore
must not be automatically retried after uncertain responses. Inspection needs no
operation request:

```bash
snook-cli call create-board '{"name":"Client work"}'
snook-cli call create-project '{"boardId":"<board-id>","name":"September release","starred":true}'
snook-cli call create-activity-group '{"name":"Deep work"}'
snook-cli call create-activity '{"name":"Writing","defaultLane":"Foreground","groupId":"<group-id>"}'
snook-cli call create-task '{"projectId":"<project-id>","title":"Draft launch note","priority":"High","dueDate":"2026-09-20"}'
snook-cli call get-task-details '{"taskId":"<task-id>"}'
snook-cli call add-task-tag '{"taskId":"<task-id>","displayName":"release","color":"#34C58A"}'
snook-cli call add-task-link '{"taskId":"<task-id>","label":"Brief","uri":"https://example.test/brief","kind":"reference"}'
snook-cli call add-task-dependency '{"taskId":"<task-id>","prerequisiteTaskId":"<other-task-id>"}'
```

## Journals and entries

The following commands work in embedded and authenticated daemon modes. Use
`--host daemon` while the desktop/daemon owns the workspace. Generate real UUIDs
for the placeholders; creation omits `expectedRevision`, while updates and
delete/restore require the revision returned by a read or successful write.
Keep the exact operation ID and payload when retrying an uncertain write.

```bash
snook-cli journals
snook-cli journals --include-deleted
snook-cli call create-journal '{"definition":{"name":"Everyday","description":"Small moments"},"request":{"operationId":"<new-guid>","clientDeviceId":"<device-guid>"}}'
snook-cli call update-journal '{"journalId":"<journal-id>","definition":{"name":"Personal","description":"Small moments"},"request":{"operationId":"<new-guid>","clientDeviceId":"<device-guid>","expectedRevision":1}}'

snook-cli call create-journal-entry '{
  "definition":{
    "journalId":"<journal-id>","title":"A slower morning",
    "content":"Coffee by the window.\nA little time to think.",
    "occurredAtUtc":"2026-09-18T14:00:00Z","mood":6,"tags":["gratitude","small wins"]
  },
  "request":{"operationId":"<new-guid>","clientDeviceId":"<device-guid>"}
}'
snook-cli journal-entries '{"journalId":"<journal-id>","search":"coffee","tag":"gratitude","pageSize":30}'
snook-cli call get-journal-entry '{"entryId":"<entry-id>"}'
snook-cli call get-journal-tags '{"journalId":"<journal-id>"}'

snook-cli call update-journal-entry '{
  "entryId":"<entry-id>",
  "definition":{
    "journalId":"<journal-id>","title":"A slower morning","content":"Revised words.",
    "occurredAtUtc":"2026-09-18T14:00:00Z","mood":null,"tags":[]
  },
  "request":{"operationId":"<new-guid>","clientDeviceId":"<device-guid>","expectedRevision":1}
}'
snook-cli call set-journal-entry-deleted '{"entryId":"<entry-id>","deleted":true,"request":{"operationId":"<new-guid>","clientDeviceId":"<device-guid>","expectedRevision":2}}'
snook-cli call set-journal-entry-deleted '{"entryId":"<entry-id>","deleted":false,"request":{"operationId":"<new-guid>","clientDeviceId":"<device-guid>","expectedRevision":3}}'
snook-cli call set-journal-deleted '{"journalId":"<journal-id>","deleted":true,"request":{"operationId":"<new-guid>","clientDeviceId":"<device-guid>","expectedRevision":2}}'
snook-cli call set-journal-deleted '{"journalId":"<journal-id>","deleted":false,"request":{"operationId":"<new-guid>","clientDeviceId":"<device-guid>","expectedRevision":3}}'
```

An entry update replaces all editable fields. Change `journalId` to move it;
`mood: null` removes a rating, and `tags: []` removes its tags. Titles may be blank
and have a 200-character limit; content is required and limited to 20,000
characters. Mood is optional and must be an integer 1–7 when supplied. Tags are
trimmed and deduplicated without regard to case, limited to 20 names of 50
characters each, with no commas inside a name. They are separate from all other
Snook tags. Journal names allow 100 characters; descriptions allow 1000.

`journal-entries` returns `{items, continuationToken, hasMore}` in newest-first
occurrence order, with an ID tie-breaker. `JournalEntryQuery` accepts optional
`journalId`, literal text `search` (title and body, up to 200 characters), exact
case-insensitive `tag`, `includeDeleted`, `pageSize` (1–100, default 30), and
`continuationToken`. Pass the returned token with the same filters for the next
page. Omit `journalId` to search all journals. `includeDeleted: true` also includes
entries in deleted journals; direct `get-journal-entry` can read a deleted entry.
Restore a deleted journal before editing or restoring its entries.
`get-journal-tags` lists active-entry tags only, optionally scoped to one journal,
with a limit of 1000 distinct names. The `api` catalog describes all methods and
the journal record/query fields.

## Safe mutations and retries

Revision-checked operations require an `OperationRequest` named `request`.
Fetch the current entity first, preserve its `revision`, generate an operation
ID for the intended logical change, and reuse that same operation ID if the
client times out and retries. This preserves mutation receipt idempotency.

```bash
snook-cli call update-task '{
  "taskId":"<task-id>",
  "update":{"title":"Draft launch note","description":"First pass","priority":"High","dueDate":"2026-09-20","defaultActivityId":null,"starred":true},
  "request":{"operationId":"<new-guid>","clientDeviceId":"<stable-client-guid>","expectedRevision":4}
}'

snook-cli call complete-task '{
  "taskId":"<task-id>",
  "request":{"operationId":"<same-guid-on-retry>","clientDeviceId":"<stable-client-guid>","expectedRevision":5}
}'
```

The same request pattern applies to update, reorder, archive, restore, delete,
timer lifecycle, correction, recovery, and calendar-exception operations. A
stale revision returns a structured domain error; retain the agent's draft,
reload the entity, and explicitly reconcile it instead of blindly overwriting.
Creation/add methods accept the caller request described above without an expected
revision; consult `snook-cli api` for each exact signature. Upgrade daemon and
clients together: 1.6 clients refuse a daemon older than 1.6. Older wire clients
can omit the new optional arguments when talking to a 1.6 daemon. In-process C#
callers must rebuild; use named `cancellationToken:` where a create call previously
passed its token positionally, since `request` now precedes the final token.

## Tracking and calendar examples

```bash
# Start a foreground timer and later stop it using the returned revision.
snook-cli call start-session '{"taskId":"<task-id>","activityId":"<activity-id>","lane":"Foreground","request":{"operationId":"<guid>","clientDeviceId":"<guid>","expectedRevision":null}}'
snook-cli call stop-session '{"sessionId":"<session-id>","notes":"Finished draft","request":{"operationId":"<guid>","clientDeviceId":"<guid>","expectedRevision":1}}'

# Enter prior work without a running timer.
snook-cli call create-manual-session '{"taskId":"<task-id>","activityId":"<activity-id>","startedAtUtc":"2026-09-13T14:00:00Z","endedAtUtc":"2026-09-13T14:45:00Z","notes":"Review"}'

snook-cli call create-calendar-event '{"calendarId":"<calendar-id>","title":"Planning","startAtUtc":"2026-09-14T16:00:00Z","endAtUtc":"2026-09-14T16:30:00Z","timeZone":"America/Chicago","recurrenceRule":"FREQ=WEEKLY;INTERVAL=1","recurrenceEndUtc":"2026-10-12T16:00:00Z"}'
snook-cli call create-schedule-block '{"calendarId":"<calendar-id>","taskId":"<task-id>","activityId":"<activity-id>","titleOverride":"Focus time","startAtUtc":"2026-09-14T18:00:00Z","endAtUtc":"2026-09-14T19:00:00Z","timeZone":"America/Chicago"}'
```

The backend validates intervals, time zones, recurrence bounds, IDs, and request
sizes. Calendar range methods expand only bounded recurrence and require an end
date in a recurrence rule/end field as documented by the API.

## Backup, export, and restore

These use the same ownership and integrity paths as the desktop settings UI.
JSON export currently returns schema 5; its file encoding differs from CLI
response encoding. See the [export format reference](json-export.md) for sections,
numeric enums, deleted records, snapshot cursor and schema-4 compatibility.
Use absolute, agent-controlled paths and keep backups with their manifests.
In daemon mode these are paths on the daemon host, not client-side downloads.
Backup and export destinations must be fully qualified (at most 4096 characters,
128 path components) and unused. No `.`/`..` traversal, linked directories/files,
special device names, workspace sidecar/owner names, or daemon credential/discovery
names are accepted. A backup also requires unused manifest and SQLite sidecar
names. Existing files are never replaced, even for a repeated identical call;
choose a fresh destination for each intentional artifact. A collision returns
`ValidationFailed`, not success/replay. After an uncertain response, inspect the
destination before starting another operation. These writes have no entity
operation receipt. Use directories under your control, not directories a different
user can rename or replace during publication.

```bash
snook-cli call create-backup '{"destinationPath":"/secure/backups/snook-2026-09-13.db"}'
snook-cli call export-json '{"destinationPath":"/secure/exports/snook.json"}'
snook-cli call export-csv '{"destinationPath":"/secure/exports/worklog.csv","rangeStartUtc":"2026-09-01T00:00:00Z","rangeEndUtc":"2026-10-01T00:00:00Z"}'
snook-cli call restore-backup '{"sourcePath":"/secure/backups/snook-2026-09-13.db"}'
```

Before restore, stop other clients as described in [operations](operations.md);
keep the selected daemon running to perform the operation. `sourcePath` must be
an absolute standalone backup file with its matching `<sourcePath>.manifest.json`.
Normal restore rejects a missing/malformed/unsupported manifest, byte-count or
SHA-256 mismatch, SQLite/schema failures, linked inputs, and live/recovery sidecars.
Inputs are bounded to 16 GiB for backups and 16 KiB for manifests. Valid older
format-1 pairs can be moved and are upgraded in private staging without changing
their source. The original path inside the manifest is informational only.
Failures use structured errors (`SchemaIncompatible` for integrity/manifest
problems, `ValidationFailed` for unsafe input selection). Do not manufacture a
manifest for an unknown or incomplete file; create a new online backup from its
known healthy owning host or recover the original manifest. Restore has no exact
receipt replay: after losing its response, inspect the current workspace first.

Running timers from the backup become frozen recovery items, not running clocks.
`bootstrap` exposes them in `today.recoverySessions`. Use `call resolve-recovery`
with `sessionId`, `decision` (`stopAtLastKnown`, `stopNow`, or `continue`) and a
fresh `request` containing the item's `expectedRevision`. Preserve that request
for retries. Last known adds no time; Stop now explicitly credits the gap; Continue
starts a new interval now without the gap. `call get-session-corrections` exposes
the before/after provenance for preparation and resolution. Full semantics and
clock rollback handling are in [restore operations](operations.md).
