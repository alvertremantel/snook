# Product and Domain Model

## Ubiquitous language

| Term | Meaning |
|---|---|
| Workspace | The local data boundary. v1 has one workspace per profile/database. |
| Board | A named top-level organization containing projects. |
| Project | A goal/deliverable containing tasks. |
| Task | A concrete unit of planned work with state, deadline, dependencies, and time history. |
| Activity | A reusable category of effort across tasks/projects, such as Coding, Reading, or Exercise. |
| Tracking session | One user-intended timer lifecycle: start through stop, including pauses. |
| Active interval | A continuous portion of a session during which time accrues. |
| Schedule block | Planned calendar time, optionally linked to a task/activity; distinct from a task deadline. |
| Calendar event | Non-work or general scheduled event; may be recurring. |
| Tag | User-defined cross-cutting label. |
| Lane | Foreground or background tracking classification. |
| Backend host | Process that runs application operations and owns the database: embedded app or daemon. |
| Device | Cryptographic/sync identity, not merely a hostname. |

The UI must explain Activity as “what kind of effort” and Task as “what outcome
you are advancing.” A tracking session can reference a task, an activity, or
both. At least one reference is required.

## Core relationships

```text
Workspace
  ├─ Board 1─* Project 1─* Task
  │                         ├─ *─* prerequisite Task
  │                         ├─ *─* Tag
  │                         ├─ 0─* ScheduleBlock
  │                         └─ 0─* TrackingSession
  ├─ Activity *─* ActivityGroup
  │     ├─ *─* Tag
  │     ├─ 0─* ScheduleBlock
  │     └─ 0─* TrackingSession
  ├─ Calendar 1─* CalendarEvent 1─* RecurrenceException
  └─ TrackingSession 1─* ActiveInterval
```

Projects also have tags and optional default activities. A task may override
its project's default activity. Defaults reduce friction; they never replace
explicit session attribution.

## Key model improvements

### Tasks and time are integrated, not merged into one generic table

A task and an activity have different semantics, so a generic “item” table
would weaken constraints and produce nullable-column sprawl. Integration occurs
through first-class session references and application workflows:

- “Start focus” is available on every task.
- Active session state and accumulated time appear on task/project details.
- General time can still be tracked against an activity without inventing a
  task.
- A session attached to both a task and activity contributes to both projections
  without double-counting the session in global totals.
- Project totals include sessions attributed to its tasks. Unattributed
  activity time does not silently appear in a project.

### Deadline and scheduled work are separate

Legacy drag/drop synchronized task `due_date` with a one-hour event. Grouper
Next separates:

- `Task.DueAt`: commitment/deadline.
- `ScheduleBlock`: planned time allocation; a task may have zero or many.

Dragging a task to a calendar creates/moves a schedule block. Changing a block
does not change the deadline unless the user explicitly chooses “set deadline.”

### One session, many intervals

Pause does not split or replace a session. It closes the current interval;
resume opens another. Session identity, notes, corrections, and attribution
remain stable.

## State models

### Task

```text
Open ──complete──> Completed
  │                    │
  ├──archive/delete    ├──reopen──> Open
  v                    v
Archived/Deleted     Archived/Deleted
```

- A task cannot complete while an active prerequisite is incomplete unless the
  user invokes an explicit, audited override (post-v1 candidate).
- All completion entry points call one domain command that atomically maintains
  status and `CompletedAt`.
- Archival and soft deletion are distinct. Both remove a task from normal active
  views while preserving time and historical references; archive is ordinary
  organization, while delete feeds recovery/trash. Permanent purge is a
  separate maintenance action.
- Dependency cycles are rejected transactionally.

### Tracking session

```text
          start
            v
        Running <──resume── Paused
           │                  │
        pause                stop
           │                  │
           └──────────────> Stopped

Running ──stop─────────────> Stopped
Running/Paused ──recover───> RecoveryRequired or Stopped(estimated)
```

Transition invariants:

- Start creates the session and one open active interval in one transaction.
- Pause closes exactly one open interval and sets the session paused.
- Resume opens exactly one interval and sets the session running.
- Stop closes an open interval if present and seals the session.
- Duplicate/retried commands are idempotent by operation ID.
- Invalid expected-state transitions fail with a typed conflict; they do not
  “best effort” mutate.
- Notes/attribution edits do not rewrite interval boundaries.
- Manual time entry creates a stopped session with one closed interval and
  provenance `manual`.
- Corrections preserve original values in an audit record and update canonical
  intervals transactionally.

Concurrency policy is an application rule, not a fragile schema assumption.
Foreground concurrency is a user setting: when disabled, starting a new
foreground session pauses the currently running foreground session; when
enabled, multiple foreground sessions may run. Background-session behavior
remains a separate lane, while paused sessions and overlaps from external
records are preserved. See `open-questions.md`.

## Primary workflows

### Capture and work a task

1. User creates a task from board, list, dashboard, calendar, or quick capture.
2. Task is assigned to a project and may inherit activity, tags, and priority.
3. User optionally sets a deadline and one or more schedule blocks.
4. “Start focus” begins a session already attributed to the task and inferred
   activity; no stop-time attribution dialog is required.
5. Timer appears globally and locally on the task.
6. Pause/resume/stop update only lifecycle boundaries.
7. Task/project/dashboard/history/summary update from one committed change
   notification.

### Track general activity

1. User selects or searches an activity from quick start.
2. Start creates an activity-only session.
3. User can attach a task while running or after stop without losing identity.
4. Groups/tags organize activities; no hard limit of three groups is imposed.

### Recover after interruption

1. Backend starts and loads sessions left running/paused.
2. If shutdown was clean, sessions remain in their durable state.
3. If duration is uncertain (clock rollback, database restore, or an incomplete
   active row), UI shows a recovery card rather than inventing hidden time.
4. User chooses stop-at-last-known, stop-now, continue, or edit boundaries.
5. Choice is persisted with recovery provenance.

### Plan on the calendar

1. User drags a task to a time range or creates a block from task detail.
2. The schedule block links to the task and optionally an activity.
3. Starting from the block inherits those links.
4. Completing/deleting a task does not silently delete historical blocks;
   future blocks prompt for cancel/retain.

### Review

- Dashboard answers: what is running, what should I do, and what is scheduled?
- History is searchable, filterable, paginated, and editable by type/date/
  project/task/activity/tag.
- Summary can aggregate by task, project, activity, group, tag, day, and lane.
- Concurrent interval reporting distinguishes attributed duration (sum) from
  wall-clock coverage (union), preventing misleading totals.

## UI information architecture

Desktop/tablet baseline:

- Today (dashboard + quick capture + agenda)
- Tasks (board/list switch)
- Track (quick activities + active/paused sessions)
- Calendar
- Review (history + summary)
- Sync/Devices (only when capability is enabled)
- Settings/Data

Phone uses bottom navigation for Today, Tasks, Track, and More. Detail/edit
surfaces become routed pages or sheets rather than compressed desktop panels.
The UI may preserve the legacy dark visual character, but native window chrome,
responsive layout, semantic controls, keyboard navigation, and screen-reader
names take precedence over custom frameless effects.

## Deletion and retention policy

- User content defaults to soft delete with `DeletedAt` and can be restored.
- Archive hides long-lived organizational entities without calling them deleted.
- Permanent purge is explicit, previews dependent data, and is unavailable
  while unsynchronized tombstone retention requires the entity.
- Time interval audit/correction history has a defined retention policy and is
  included in export.
- Device-local secrets, window state, and daemon credentials are never synced as
  workspace settings.
