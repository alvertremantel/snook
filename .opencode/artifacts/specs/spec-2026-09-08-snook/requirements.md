# Requirements

`MUST`, `SHOULD`, and `MAY` are normative. IDs remain stable as the package is
revised. Unless marked post-v1, MUST requirements gate production release.

## Functional requirements

### Workspace and organization

- **FR-ORG-001 MUST:** Support one local workspace per configured profile.
- **FR-ORG-002 MUST:** Create, rename, archive, restore, reorder, and soft-delete
  boards, projects, tasks, activities, groups, calendars, and user events as
  applicable.
- **FR-ORG-003 MUST:** Use stable IDs independent of mutable names.
- **FR-ORG-004 MUST:** Support tags on projects, tasks, and activities.
- **FR-ORG-005 MUST:** Support URL and file references on tasks. File references
  are pointers; Snook does not silently copy file content.
- **FR-ORG-006 MUST:** Make name uniqueness scope/case rules explicit and report
  conflicts without swallowing errors.
- **FR-ORG-007 SHOULD:** Support user-defined ordering without encoding order in
  names or timestamps.

### Tasks and projects

- **FR-TASK-001 MUST:** Provide board and flat-list task views with filtering,
  sorting, search, completed visibility, and project grouping.
- **FR-TASK-002 MUST:** Store title, description, project, priority, deadline,
  status, star, tags, timestamps, and optional default activity.
- **FR-TASK-003 MUST:** Support task dependencies, reject self/cyclic edges, and
  block normal completion while prerequisites remain open.
- **FR-TASK-004 MUST:** Centralize complete/reopen transitions and atomically
  maintain completion timestamps.
- **FR-TASK-005 MUST:** Move tasks between projects transactionally.
- **FR-TASK-006 MUST:** Expose active and historical tracked time on task and
  project details.
- **FR-TASK-007 MUST:** Start a correctly attributed timer directly from a task.
- **FR-TASK-008 SHOULD:** Offer quick capture with keyboard-first desktop flow
  and low-friction mobile flow.
- **FR-TASK-009 SHOULD:** Preserve starred/pinned projects and tasks as inputs to
  Today/Taskbox projections.

### Time tracking

- **FR-TIME-001 MUST:** Support start, pause, resume, stop, manual entry, edit,
  notes, and attribution to task and/or activity.
- **FR-TIME-002 MUST:** Require at least one task or activity target per session.
- **FR-TIME-003 MUST:** Represent one session with one or more active intervals;
  pause/resume never destroys/replaces the session.
- **FR-TIME-004 MUST:** Persist only start/pause/resume/stop, explicit edits,
  recovery decisions, and sync applications—never display ticks.
- **FR-TIME-005 MUST:** Render active elapsed time in memory from an initial
  snapshot and clock; embedded mode performs no periodic DB polling.
- **FR-TIME-006 MUST:** Enforce lifecycle transitions atomically against an
  expected state and return typed conflicts for stale clients.
- **FR-TIME-007 MUST:** Make retried mutation requests idempotent.
- **FR-TIME-008 MUST:** Restore durable active/paused state after process restart
  and provide explicit recovery when accuracy is uncertain.
- **FR-TIME-009 MUST:** Support foreground/background lanes and a user setting
  for foreground concurrency; when disabled, starting a new foreground session
  pauses the currently running foreground session.
- **FR-TIME-010 MUST:** Calculate both attributed-duration and wall-clock-coverage
  metrics where concurrency matters.
- **FR-TIME-011 MUST:** Allow attribution and notes to be corrected without
  changing session identity.
- **FR-TIME-012 MUST:** Keep correction provenance sufficient to explain changed
  time totals.
- **FR-TIME-013 SHOULD:** Start/stop common timers from global shortcuts where
  the OS permits; Android shortcuts/widgets are platform-gated.

### Calendar and review

- **FR-CAL-001 MUST:** Provide day/agenda, week, and month calendar views;
  timeline is SHOULD for desktop.
- **FR-CAL-002 MUST:** Keep task deadlines independent from schedule blocks.
- **FR-CAL-003 MUST:** Create/move/resize schedule blocks linked to tasks and/or
  activities and start tracking from a block.
- **FR-CAL-004 MUST:** Support user calendars and events with time range,
  all-day, description, location, color, visibility, and recurrence.
- **FR-CAL-005 MUST:** Apply recurrence end conditions and occurrence exceptions
  consistently and bound expansion queries.
- **FR-CAL-006 MUST:** Represent task deadlines and tracked sessions as read-only
  projections, not fake mutable system-calendar rows.
- **FR-REV-001 MUST:** Dashboard/Today combines active timers, priority tasks,
  upcoming deadlines, schedule, and recent tracked work.
- **FR-REV-002 MUST:** History is searchable, filterable, paginated, and does not
  use a hidden fixed result limit.
- **FR-REV-003 MUST:** Summary supports ranges and grouping by day, task, project,
  activity, activity group, tag, and lane where meaningful.
- **FR-REV-004 MUST:** Reports split interval contribution at requested local-day
  boundaries without mutating stored intervals.
- **FR-REV-005 MUST:** Exclude paused time and avoid double-counting one session
  linked to both a task and activity in global totals.

### Host modes, client, CLI, and daemon

- **FR-HOST-001 MUST:** Expose application use cases through one versioned backend
  contract independent of persistence and UI.
- **FR-HOST-002 MUST:** Desktop support embedded mode and daemon-client mode.
- **FR-HOST-003 MUST:** Daemon-only/client-only profiles never silently fall back
  to opening the DB when the daemon is unavailable.
- **FR-HOST-004 MUST:** Only one logical backend host own a database at a time;
  startup conflicts are detected before writes.
- **FR-HOST-005 MUST:** Windows/Linux daemon run interactively for diagnostics and
  as a managed service with graceful shutdown/readiness.
- **FR-HOST-006 MUST:** CLI invoke the same contract and provide structured JSON
  output plus stable exit/error codes.
- **FR-HOST-007 MUST:** Client reconnect obtains a fresh snapshot/cursor then
  resumes ordered change notifications without requiring timer polling.
- **FR-HOST-008 SHOULD:** Android support both embedded and remote daemon-client
  profiles, subject to the real-device feasibility gate.
- **FR-HOST-009 MUST NOT:** Assume a permanently running desktop-style daemon on
  Android.

### Data ownership, backup, export, sync

- **FR-DATA-001 MUST:** Use platform-appropriate private application data paths,
  with one authoritative path policy per platform.
- **FR-DATA-002 MUST:** Support online, consistent, user-triggered backup and
  verified restore; daemon mode performs these through the daemon.
- **FR-DATA-003 MUST:** Allow supported data relocation only while ownership is
  exclusive and after an integrity-checked backup.
- **FR-DATA-004 MUST:** Export a documented, versioned JSON archive and useful
  CSV reports without requiring network access.
- **FR-SYNC-001 MUST:** Local operation remain complete when sync is disabled.
- **FR-SYNC-002 MUST:** If shipped, peer sync require explicit pairing,
  authenticated encryption, authorization, replay protection, and revocation.
- **FR-SYNC-003 MUST:** Apply remote operations through the same domain/integrity
  boundary or a deliberately constrained replication boundary—never arbitrary SQL.
- **FR-SYNC-004 MUST:** Preserve deletions/tombstones and unresolved concurrent
  values long enough for deterministic convergence and user resolution.
- **FR-SYNC-005 MUST:** Device-local configuration and secrets never replicate.
- **FR-SYNC-006 SHOULD:** Discovery reveal minimal metadata and be independently
  disableable; discovery never grants trust.

## Non-functional requirements

### Correctness and durability

- **NFR-COR-001 MUST:** SQLite foreign keys and integrity checks are enabled on
  every connection; write transactions use explicit rollback behavior.
- **NFR-COR-002 MUST:** Migrations are ordered, checksumed, transactional where
  SQLite allows, backup-gated, and tested from real prior schema fixtures.
- **NFR-COR-003 MUST:** A process kill at any instruction boundary leaves either
  the old committed state or new committed state, not a partial lifecycle.
- **NFR-COR-004 MUST:** Cross-midnight, leap-day, DST gap/fold, time-zone change,
  clock rollback, and long-running session tests have explicit expected results.
- **NFR-COR-005 MUST:** Store instants in UTC and retain the user/report time-zone
  context needed to derive civil dates; never depend on lexical naive-time math.
- **NFR-COR-006 MUST:** Startup detect corruption/incompatible schema without
  replacing the user's DB or silently resetting to defaults.
- **NFR-COR-007 MUST:** Backup/restore use SQLite-supported snapshot mechanisms,
  validate integrity, and include a manifest.

### Performance and resource use

- **NFR-PERF-001 MUST:** A running timer causes zero durable writes between
  lifecycle boundaries.
- **NFR-PERF-002 MUST:** Embedded timer display causes zero DB reads between
  change/reconciliation events.
- **NFR-PERF-003 MUST:** Daemon timer clients receive push changes and MUST NOT
  poll backend state once per second.
- **NFR-PERF-004 SHOULD:** On reference desktop hardware with 50k tasks and 1m
  active intervals, common list/dashboard queries achieve p95 under 200 ms after
  warm-up; measurement fixture and hardware are recorded.
- **NFR-PERF-005 SHOULD:** Lifecycle command commit p95 is under 100 ms on local
  storage under the supported single-writer workload.
- **NFR-PERF-006 MUST:** Calendar recurrence expansion and history APIs are
  range-bounded, paginated, cancellable, and protected from unbounded allocation.
- **NFR-PERF-007 MUST:** Android background work obey OS battery/network limits.

### Security and privacy

- **NFR-SEC-001 MUST:** Default profile starts no externally reachable listener
  and makes no telemetry, update-check, or sync request without opt-in.
- **NFR-SEC-002 MUST:** Local IPC use OS identity/ACL protection; TCP fallback
  uses a high-entropy credential and loopback binding.
- **NFR-SEC-003 MUST:** Remote APIs use modern TLS and device/user authorization;
  plaintext legacy NDJSON is not supported.
- **NFR-SEC-004 MUST:** Secrets are stored through platform adapters or files
  restricted to the owning service identity; never in workspace settings/logs.
- **NFR-SEC-005 MUST:** Logs redact task text, notes, paths, tokens, and payloads
  by default and have bounded retention.
- **NFR-SEC-006 MUST:** All inputs are size/range/type validated, including IPC,
  sync, recurrence rules, URIs, and file paths.
- **NFR-SEC-007 MUST:** Release builds include dependency/license inventory and
  vulnerability scanning with a triage policy.
- **NFR-SEC-008 SHOULD:** Optional at-rest encryption be capability-detected and
  documented accurately; absence must never be described as encryption.

### Platform, UX, and accessibility

- **NFR-UX-001 MUST:** Supported desktop OS matrix includes current supported
  Windows x64/arm64 and mainstream glibc Linux x64/arm64 targets selected before
  release; package formats are explicit.
- **NFR-UX-002 MUST:** Android minimum/target API, ABIs, and device matrix are
  versioned release criteria; API 24 is the initial proposed minimum.
- **NFR-UX-003 MUST:** Core flows are usable with keyboard on desktop and screen
  readers/touch on applicable platforms, with semantic names and logical focus.
- **NFR-UX-004 MUST:** Text scales without clipping at supported OS/font scaling;
  color is not the sole state indicator; reduced motion is respected.
- **NFR-UX-005 MUST:** Responsive layouts do not reproduce fixed desktop
  quadrants on phone-sized displays.
- **NFR-UX-006 MUST:** User-visible mutations provide success/error/conflict
  feedback and never rely only on console traceback.
- **NFR-UX-007 MUST:** Avalonia Android remains behind an explicit quality gate
  while its support is labeled experimental upstream.

### Engineering and operability

- **NFR-ENG-001 MUST:** Domain has no Avalonia, SQLite, transport, or OS service
  dependencies; composition roots point inward.
- **NFR-ENG-002 MUST:** Nullable reference types, analyzers, warnings-as-errors,
  deterministic builds, formatting, tests, and dependency pinning run in CI.
- **NFR-ENG-003 MUST:** Contract tests execute against embedded and daemon-backed
  implementations.
- **NFR-ENG-004 MUST:** Daemon exposes authenticated health/readiness and
  structured diagnostics without exposing user content.
- **NFR-ENG-005 MUST:** Logs include correlation/operation IDs and transition
  outcomes but not content by default.
- **NFR-ENG-006 MUST:** Service startup, shutdown, update, lock ownership, backup,
  restore, and migration failures have documented operator procedures.
- **NFR-ENG-007 MUST:** Public contracts are versioned with backward-compatibility
  policy; mismatches fail clearly.

## Release acceptance scenarios

| ID | Scenario | Pass condition |
|---|---|---|
| AC-01 | Run timer for 24 h | Correct display; no tick reads/writes; one open interval |
| AC-02 | Pause/resume 100 times | 101 valid intervals; no lost/negative time; one session identity |
| AC-03 | Kill during each transition | DB is pre- or post-command and passes integrity checks |
| AC-04 | Span midnight/DST | Reports allocate exact overlap; stored record is not split |
| AC-05 | Two clients send same command | Idempotent result; no duplicate interval/outbox record |
| AC-06 | Embedded vs daemon suite | Equivalent response/error/change semantics |
| AC-07 | Daemon unavailable | Client-only UI explains/retries; never opens DB directly |
| AC-08 | Embedded/daemon collision | Second host refuses ownership before mutation |
| AC-10 | Restore interruption | Original active DB remains usable or rollback is automatic |
| AC-11 | Unpaired network peer | No metadata or workspace data disclosed; connection rejected |
| AC-12 | Android lifecycle | Active state reconciles after suspend/kill without background tick service |
| AC-13 | Accessibility smoke | Core flows pass keyboard/screen-reader/scale checklist |
| AC-14 | Large fixture | Performance targets measured without N+1 or unbounded recurrence behavior |
