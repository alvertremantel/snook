# Snook headless CLI

`snook` is the headless, structured-JSON frontend for Snook's public backend
contract. It is designed for shell scripts and agents: successful values are
JSON on standard output, errors are JSON on standard error, and it does not
render human-oriented tables that need reparsing.

## Running it

During development, invoke the project directly:

```bash
dotnet run --project src/Snook.Cli/Snook.Cli.csproj -- [global options] command
```

Global options precede the command.

| Option | Environment fallback | Meaning |
| --- | --- | --- |
| `--data-dir PATH` | `SNOOK_DATA_DIR` | Root containing the `Snook` data directory. |
| `--host embedded\|daemon` | `SNOOK_HOST_MODE` | Select SQLite ownership or remote daemon transport. |
| `--endpoint URL` | `SNOOK_DAEMON_ENDPOINT` | Absolute daemon HTTP(S) endpoint. |
| `--token TOKEN` | `SNOOK_DAEMON_TOKEN` | Daemon authentication token. Prefer a token file. |
| `--token-file PATH` | — | Private file containing the daemon token. |

Embedded mode is the default and opens `<data-dir>/Snook/workspace.db`.
Daemon mode uses the endpoint and token above; when no token is supplied it
reads `<data-dir>/Snook/daemon.token`. It never opens SQLite, including after an
authentication or connection failure. A daemon currently means the authenticated
loopback `snookd` host, but this boundary is intentionally the configuration
point for a later server implementation.

Exit codes are `0` for success, `2` for a domain or validation error, `64` for
CLI syntax/argument errors, `1` for an unexpected host/transport error, and
`130` when a caller cancels the command.

## Discovering the contract

```bash
snook api > snook-api.json
snook doctor
snook bootstrap
```

`api` is the authoritative machine-readable catalog. It includes every callable
`IBackendClient` method, its exact named arguments, defaults, and return type.
It is generated from the interface, so additions to the desktop/backend contract
automatically appear in the CLI. `doctor` verifies the selected host and reports
the workspace and advertised capabilities. `bootstrap` returns the complete
initial workspace projection: hierarchy, settings, today state, calendars,
deleted items, capabilities, and committed cursor.

`watch` is the streaming counterpart for the `IBackendClient.Changed` event. It
emits newline-delimited JSON (NDJSON): one `{ "type": "ready" }` record with
the initial cursor, then `{ "type": "change" }` records after successful
commits. It runs until the caller cancels it (normally Ctrl-C) and is the
appropriate daemon-mode replacement for polling SQLite.

Convenience read commands are available for common automation:

```bash
snook tasks [search]
snook summary day|task|project|activity|activitygroup|tag|lane [days]
snook calendar blocks|events '{"rangeStartUtc":"2026-09-13T00:00:00Z","rangeEndUtc":"2026-09-20T00:00:00Z"}'
```

`history` optionally accepts a `HistoryQuery` JSON object, for example:

```bash
snook history '{"rangeStartUtc":"2026-09-01T00:00:00Z","rangeEndUtc":"2026-10-01T00:00:00Z","search":"planning","pageSize":100}'
```

`calendar` defaults to the next seven days when the range object is omitted.

## Planning and applying task batches

Use the dedicated two-step command for mass edits. `plan` resolves a selection to
exact task IDs and revisions without writing anything, then returns a JSON document
that includes a readable preview, the patch, and stable operation/device IDs.
Review and retain that document; `apply` accepts it unchanged and commits the whole
batch atomically.

```bash
snook task-batch plan '{
  "selection":{"projectId":"<project-id>","search":"release","status":"Open"},
  "update":{
    "priority":"Urgent",
    "changeDueDate":true,
    "dueDate":"2026-09-20",
    "starred":true,
    "tagsToAdd":["release"]
  }
}' > /tmp/snook-release-plan.json

snook task-batch apply "$(cat /tmp/snook-release-plan.json)"
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
snook call METHOD '{"namedArgument":"value"}'
```

For example, these command spellings identify the same method:
`create-task`, `CreateTask`, and `CreateTaskAsync`. Enumerations are string
names (`"High"`, `"Foreground"`, `"Earlier"`, `"StopNow"`). GUIDs, dates, and
timestamps are standard JSON strings; timestamps should be explicit UTC ISO 8601
instants. Use `null` for nullable values, never an empty UUID placeholder.

Creation and inspection examples:

```bash
snook call create-board '{"name":"Client work"}'
snook call create-project '{"boardId":"<board-id>","name":"September release","starred":true}'
snook call create-activity-group '{"name":"Deep work"}'
snook call create-activity '{"name":"Writing","defaultLane":"Foreground","groupId":"<group-id>"}'
snook call create-task '{"projectId":"<project-id>","title":"Draft launch note","priority":"High","dueDate":"2026-09-20"}'
snook call get-task-details '{"taskId":"<task-id>"}'
snook call add-task-tag '{"taskId":"<task-id>","displayName":"release","color":"#34C58A"}'
snook call add-task-link '{"taskId":"<task-id>","label":"Brief","uri":"https://example.test/brief","kind":"reference"}'
snook call add-task-dependency '{"taskId":"<task-id>","prerequisiteTaskId":"<other-task-id>"}'
```

## Safe mutations and retries

Revision-checked operations require an `OperationRequest` named `request`.
Fetch the current entity first, preserve its `revision`, generate an operation
ID for the intended logical change, and reuse that same operation ID if the
client times out and retries. This preserves mutation receipt idempotency.

```bash
snook call update-task '{
  "taskId":"<task-id>",
  "update":{"title":"Draft launch note","description":"First pass","priority":"High","dueDate":"2026-09-20","defaultActivityId":null,"starred":true},
  "request":{"operationId":"<new-guid>","clientDeviceId":"<stable-client-guid>","expectedRevision":4}
}'

snook call complete-task '{
  "taskId":"<task-id>",
  "request":{"operationId":"<same-guid-on-retry>","clientDeviceId":"<stable-client-guid>","expectedRevision":5}
}'
```

The same request pattern applies to update, reorder, archive, restore, delete,
timer lifecycle, correction, recovery, and calendar-exception operations. A
stale revision returns a structured domain error; retain the agent's draft,
reload the entity, and explicitly reconcile it instead of blindly overwriting.
Creation methods that do not accept a request use the backend's normal creation
semantics; consult `snook api` for each exact signature.

## Tracking and calendar examples

```bash
# Start a foreground timer and later stop it using the returned revision.
snook call start-session '{"taskId":"<task-id>","activityId":"<activity-id>","lane":"Foreground","request":{"operationId":"<guid>","clientDeviceId":"<guid>","expectedRevision":null}}'
snook call stop-session '{"sessionId":"<session-id>","notes":"Finished draft","request":{"operationId":"<guid>","clientDeviceId":"<guid>","expectedRevision":1}}'

# Enter prior work without a running timer.
snook call create-manual-session '{"taskId":"<task-id>","activityId":"<activity-id>","startedAtUtc":"2026-09-13T14:00:00Z","endedAtUtc":"2026-09-13T14:45:00Z","notes":"Review"}'

snook call create-calendar-event '{"calendarId":"<calendar-id>","title":"Planning","startAtUtc":"2026-09-14T16:00:00Z","endAtUtc":"2026-09-14T16:30:00Z","timeZone":"America/Chicago","recurrenceRule":"FREQ=WEEKLY;INTERVAL=1","recurrenceEndUtc":"2026-10-12T16:00:00Z"}'
snook call create-schedule-block '{"calendarId":"<calendar-id>","taskId":"<task-id>","activityId":"<activity-id>","titleOverride":"Focus time","startAtUtc":"2026-09-14T18:00:00Z","endAtUtc":"2026-09-14T19:00:00Z","timeZone":"America/Chicago"}'
```

The backend validates intervals, time zones, recurrence bounds, IDs, and request
sizes. Calendar range methods expand only bounded recurrence and require an end
date in a recurrence rule/end field as documented by the API.

## Backup, export, and restore

These use the same ownership and integrity paths as the desktop settings UI.
Use absolute, agent-controlled paths and keep backups with their manifests.

```bash
snook call create-backup '{"destinationPath":"/secure/backups/snook-2026-09-13.db"}'
snook call export-json '{"destinationPath":"/secure/exports/snook.json"}'
snook call export-csv '{"destinationPath":"/secure/exports/worklog.csv","rangeStartUtc":"2026-09-01T00:00:00Z","rangeEndUtc":"2026-10-01T00:00:00Z"}'
snook call restore-backup '{"sourcePath":"/secure/backups/snook-2026-09-13.db"}'
```

Before restore, stop other workspace owners as described in
[operations](operations.md). Restore replaces workspace state only after the
source passes integrity checks; the CLI does not weaken those safeguards.
