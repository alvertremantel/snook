# Snook implementation progress

Updated: 2026-09-18

## Current state

Snook is a working desktop vertical slice for local work organization and time tracking. It builds as a .NET 10 Avalonia application, persists to SQLite, and can optionally use an authenticated local daemon. The UI is ready for a human visual pass; this is not yet a finished multi-platform release.

## Implemented surface

- Saved version-1 client profiles now let both the GUI and CLI select embedded or
  authenticated daemon mode without repeating connection flags. Profiles retain
  only host/data-directory/endpoint/token-file settings, use private atomic writes,
  and never serialize the token. Resolution is flags, environment, saved profile,
  then the embedded default; malformed/unsafe profiles fail closed, with explicit
  `--no-profile` recovery for the CLI. The desktop has visible startup correction,
  saved next-launch connection settings, actual endpoint/status display, an outage
  warning and manual retry without overwriting an open draft. The CLI `doctor`
  reports the resolved discovered endpoint. Desktop launcher actions expose daemon
  startup and connection settings. Real disposable daemon checks passed saved-profile
  CLI use plus GUI connection, task capture and timer controls without creating a
  client SQLite database. Full build has zero warnings/errors and all 149 tests pass
  (131 application, 18 domain), including connection-profile security/precedence,
  pending-window disposal and non-pumping UI synchronization-context regression
  coverage. Seeded and empty 980×640 captures were inspected. A controlled actual
  daemon stop/restart showed the stale-data warning, rejected refresh while offline,
  reconnected at the same endpoint and retained the typed draft; both 980×640 frames
  were inspected. Live credential/endpoint switching and broader update/lifecycle
  retry coverage remain open. No RPM was rebuilt;
  release 3 predates this connection-profile and later restore work. See the current
  [production-readiness summary](production-readiness.md) and the detailed
  [daemon plan](daemon-plan.md).

- Restored running timers now become explicit recovery items with open intervals
  frozen at the verified backup boundary. Last known adds no time; Stop now
  credits the gap explicitly; Continue starts a new interval now without the gap.
  Repeated backup/restore of unresolved items preserves the boundary, and old
  exact receipts cannot reopen current recovery state. Both preparation and
  resolution keep before/after provenance. Closed paused/stopped sessions remain
  unchanged, clock rollback cannot add negative time, and Continue honors the
  foreground concurrency preference and rejects hidden task/activity references.
  Dashboard cards show identity, recorded duration, the exact last-known boundary
  and the credit policy; unchanged cards preserve focus across refresh.
  Full build has zero warnings/errors; all 137 tests pass (119 application,
  18 domain). Shared foreground/background scenarios pass in embedded and actual
  daemon modes; additional deterministic tests cover restart, clock rollback,
  closed-session preservation, concurrency policy and archived/deleted activity
  rejection. Fresh 980×640 seeded, empty and restored screenshots were inspected;
  pointer-driven recovery checks persisted all three decisions with provenance.
  See [daemon-plan.md](daemon-plan.md#restored-timer-reconciliation-pass) for
  evidence paths and remaining acceptance. No package was rebuilt or installed;
  release 3 still predates these restore changes.

- Restore now requires and verifies the matching format-1 manifest, staged byte
  count and SHA-256 before SQLite validation/upgrade. Inputs reject links and
  live/recovery sidecars, with 16 GiB backup and 16 KiB manifest bounds. Moved
  valid pairs and older checked schema upgrades remain supported. Native connection
  leases exclude reads during replacement; disposal drains connections before
  releasing ownership. Restore invalidations use the ordered commit queue with
  captured metadata, and cancellation after commit does not fail a saved result.
  Missing active databases with restore recovery files fail startup rather than
  creating an empty workspace. Full build has zero warnings/errors; all 125 tests
  pass (107 application, 18 domain), and `git diff --check` passes. Embedded,
  daemon and CLI rejection/success scenarios, controlled connection/ownership
  tests and cancellation/observer cases passed with disposable profiles.
  Crash-consistent activation/recovery, active-timer reconciliation and broader
  daemon/GUI acceptance remain tracked in [daemon-plan.md](daemon-plan.md).
  The release-3 RPM predates these new restore changes. No UI layout changed.

- JSON export schema 5 adds persisted settings, a snapshot cursor, task links and
  dependency edges. Dedicated export reads preserve links under deleted tasks,
  stored link/tag/session tombstones, and deleted-session correction history.
  The versioned [format reference](json-export.md) documents data representation,
  numeric enums, compatibility, privacy and exclusions. The SQLite schema and
  backend contract remain 10 and 1.6. Embedded/daemon/CLI scenarios verify all
  supported feature families, relationship metadata, backup/restore and restart.
  Concurrent atomic task/tag updates retain matching titles, tags and cursors
  in each export. Full build has zero warnings/errors; all 119 tests pass
  (101 application, 18 domain), with clean whitespace and shell syntax checks.
  Release 3 was rebuilt from these sources with the format reference; extracted
  payload checks passed for create replay, schema-5 export/no-overwrite, CLI watch,
  ownership/fail-closed behavior and shutdown. Its transient per-user service
  passed readiness, protection settings, restart, stop and lease reopening.
  No package or normal service was installed/enabled. The post-publish full build
  and all 119 tests passed again. Exact artifact/evidence paths are recorded in
  [daemon-plan.md](daemon-plan.md).
  Restore verification, GUI connection/recovery, transport and native lifecycle
  acceptance remain open. No UI layout changed in this pass.

- Backup and JSON/CSV export no longer overwrite selected files. Shared embedded/
  daemon validation requires an absolute unused destination and protects live
  workspace/sidecar/lease/recovery and credential/discovery paths, including
  aliases. Backup refuses existing manifests and SQLite companions. Files are
  staged privately, verified/hashed, flushed and published without replacement;
  Unix artifacts are mode 0600. Backup now runs SQLite integrity/FK checks before
  publication. Exports hold the shared writer gate across their data queries.
  Embedded/daemon and CLI tests verify preservation of existing bytes, reserved
  paths, links, output hashes/privacy and repeated-call failures. Additional tests
  cover alias-opened workspaces, contention, pre-cancellation and integrity failure.
  Verification: full solution build with zero warnings/errors, all 115 tests pass
  (97 application, 18 domain), and `git diff --check` passes. Tests used disposable
  profiles with process/socket permissions; no real workspace or installed package
  was touched. No UI layout changed in this pass.
  Restore manifest verification, broader adversarial snapshot
  checks, power-loss/Windows acceptance and the broader daemon plan remain open.
  See [daemon-plan.md](daemon-plan.md) for the precise scope and limitations.

- Contract 1.6 exposes caller-owned operation requests for the 13 remaining
  convenience create/add methods, using shared embedded/daemon exact receipts.
  The CLI catalog exposes the optional requests; unchanged retries with a stable
  request return the original result, while changed arguments are rejected.
  GUI organization/task/calendar creation, manual time and tag/link/dependency
  additions retain their request and captured arguments while unchanged. Derived
  instants, time zones and activity defaults are frozen for retry. Successful
  writes clear attempts before refresh; late responses preserve newer drafts.
  Cancel is not an undo, and pending attempts are in memory, not a durable outbox.
  Verification: full build with zero warnings/errors, all 110 tests pass
  (92 application, 18 domain), and `git diff --check` passes. Embedded/daemon/CLI
  receipt tests and lost-response GUI model tests passed. Seeded 980×640 persisted
  editor/workspace checks and empty screenshots passed and were inspected.
  Broader update/lifecycle retries, saved GUI profiles/startup recovery and actual
  daemon-backed GUI checks remain open. The earlier RPM is contract 1.5, not a
  packaged verification of these new changes. See [daemon-plan.md](daemon-plan.md)
  for evidence paths and remaining production acceptance work.

- Fedora packaging now includes the GUI (`snook`), CLI (`snook-cli`) and daemon
  (`snookd`) as separate self-contained payloads, an opt-in per-user systemd unit,
  and operations/CLI documentation. The existing GUI launcher is preserved;
  installation does not start a service or open a workspace. Native runtime
  dependencies are explicit and bundled private libraries do not advertise
  global RPM capabilities. The build uses fresh directories, honors numeric
  version overrides and installs only its just-built artifact when requested.
  CLI help/examples use the installed command name. `watch` now emits one complete
  compact JSON object per physical line, with a live daemon regression check.
  Verification: built and inspected `snook-0.1.0-2.fc44.x86_64.rpm`; extracted
  payload checks passed for help, discovery, CLI read/write/watch, ownership and
  missing-token failures, shutdown, token permissions and embedded reopening.
  A transient user service using that package's unit settings passed readiness,
  effective protection settings, persisted mutation across restart, redacted
  journal, stop and lease reopening. No package or normal service was installed
  or enabled. Full solution build has zero warnings/errors; all 90 tests pass
  (72 application, 18 domain), and shell syntax / `git diff --check` pass. Actual
  native install/upgrade/uninstall, saved GUI profiles/outage UX, broader UI retries,
  reconnect ordering, daemon-backed GUI flows and Windows services remain open;
  see [daemon-plan.md](daemon-plan.md) for exact artifact/evidence paths.

- Schema migration 10 adds durable exact-result receipts for older organization,
  task, timer, calendar, settings and batch mutations. The transaction wrapper
  binds command arguments to the operation ID, returns the original serialized
  result on retry, and saves no-op receipts as well. Changed arguments are rejected;
  legacy receipts without verifiable original results fail explicitly. Existing
  habit/journal formats remain supported. Task-default activity resolution occurs
  inside the timer transaction after receipt lookup, preserving replay even after
  defaults change. Operation requests now validate IDs and revision applicability.
  Staged restore upgrades v9 sources without mutating them, and applied migration
  checksums from sequence 7 onward are checked independently. Database backups
  retain retry history; domain JSON/CSV exports keep their existing formats.
  Verification: clean full rebuild with zero warnings/errors and all 90 tests
  passing (72 application, 18 domain), including embedded/daemon exact replay after
  later edits/deletion, restart, backup/restore, changed argument rejection, no-op
  and create receipts, v9 migration, earlier checksum corruption, and rollback if
  receipt persistence fails. `git diff --check` passes. Tests used disposable
  profiles with local process/socket access. Caller operation IDs for remaining
  convenience creates, GUI request retention, maintenance path safety, service
  packaging and broader daemon acceptance remain tracked in the daemon plan.

- Shared commit notifications now come from the transaction's persisted mutation
  log across all entity mutation families. Receipt replay and rollback are silent;
  cursor, operation/aggregate IDs, revision, kind and millisecond UTC timestamp
  match the committed row. Delivery preserves commit order outside the write gate,
  tolerates reentrant handlers, and isolates observer exceptions from both mutation
  results and other observers. Older ad hoc publishers and duplicate habit/journal
  publishers were removed. Restore remains an explicit workspace snapshot reset.
  Verification: clean full rebuild with zero warnings/errors, all 86 tests pass
  (68 application, 18 domain), and `git diff --check` passes. Shared embedded/daemon
  tests compare notifications against SQLite through organization/tasks/tags,
  timers, settings, calendar and bulk operations, concurrent writes and retries.
  Additional tests cover restart replay, receipt-insertion rollback, sub-millisecond
  clock inputs, committed-row visibility and throwing/reentrant observers.
  Tests used disposable profiles with local process/socket access. Exact historical
  receipt results and request binding, restore/stream reconciliation, service
  packaging and remaining GUI connection work are still in the daemon plan.

- Daemon transport and client hardening is underway against the full specification;
  [daemon-plan.md](daemon-plan.md) tracks acceptance work and remaining gaps.
  The daemon now tracks and drains request handlers, serializes RPC dispatch,
  validates malformed/null arguments, sanitizes internal errors, bounds request
  concurrency and SSE queues, and uses one writer per change stream with
  heartbeats and write deadlines. Tokens are created privately, validated at
  startup, and kept out of readiness/error output. A private atomic endpoint
  descriptor enables custom-port discovery from the selected data directory.
  GUI launch flags and shared GUI/CLI connection resolution support simultaneous
  daemon clients; invalid host/port settings fail explicitly. Clients validate
  loopback endpoints and contract compatibility, bypass proxies/redirects, report
  useful connection errors, and dispose asynchronously. README/CLI/operations
  docs include connection, retry, discovery and token-rotation procedures.
  Verification: full solution build with zero warnings/errors, all 83 tests pass
  (65 application, 18 domain), and `git diff --check` passes. New transport tests
  cover malformed calls, private credentials/discovery, unsafe endpoints,
  incompatible contracts, missing-token fail-closed behavior, concurrent ordered
  changes, CLI doctor using discovery, SIGTERM and workspace lease reacquisition.
  Desktop and daemon `--help` were executed without opening a GUI/workspace.
  Tests required local process/socket access outside the sandbox and used
  disposable profiles. This is not completion of daemon production acceptance:
  legacy exact-receipt/notification semantics, broader adversarial/parity checks,
  per-user packaging, persistent GUI profiles and visible connection recovery,
  and Windows platform/service verification remain tracked work.

- Journaling is implemented as a sidebar screen with multiple journals, plain-text
  entries, separate journal-only tags, optional 1–7 mood ratings, title/body search,
  exact tag filtering, pagination, entry movement, and soft-delete/restore.
  The writing drawer preserves drafts across refreshes and conflicts, offers
  latest-saved comparison, contains keyboard focus, and supports Cancel/Escape
  with focus return. Deleted entries remain readable. Journal deletion preserves
  entries and their individual deleted states.
  Contract 1.5 provides matching embedded and authenticated daemon behavior.
  CLI helpers `journals` and `journal-entries`, plus `call` methods for every read
  and write, expose the full feature. Schema migration 9 adds independent journal
  tables; transactional content/tag writes and exact receipts survive restart,
  backup, and restore. JSON export schema 4 includes all journal data. Supported
  older backups are upgraded in staging without changing their sources.
  Limits: 200 journals including deleted records, 20,000 characters per entry,
  20 tags per entry, mood omitted or 1–7, and 1–100 entries per query page.
  Verification: full solution build with zero warnings/errors; all 71 tests pass
  (53 application, 18 domain); clean `git diff --check`. Embedded and daemon
  contract/CLI tests cover CRUD, moves, independent tags, validation, pagination,
  conflicts, request replay, notifications, migration, restart, export, and restore.
  Persisted journal UI checks pass at 1280×820 and 980×640, including create/rename,
  entry writing/movement, mood/tag clearing, filtering, draft preservation,
  stale-save review, focus, Cancel/Escape, and delete/restore. Seeded and empty
  page/editor PNGs and Today/Settings sidebar smoke captures were inspected under
  `artifacts/journals/{seeded,narrow,empty,empty-narrow,checks,checks-narrow}`.
  Tests used disposable profiles; VSTest/daemon checks required local sockets
  outside the sandbox. Native human and assistive-technology review remain
  separate release work.

- Daily habit tracking is implemented across the GUI and shared backend contract
  1.4, with a read-only `habits [HabitQuery JSON]` CLI helper and generic contract
  calls available to automation. The Habits page offers create/edit, direct daily
  check/undo, current streaks, a 30-day history, older-date review, and archive,
  soft-delete, and restore. Fixed creation time zones preserve civil days across
  DST and daemon host settings. Corrections cover the last 366 days; the initial
  catalog cap is 500 habits including deleted records. Weekday schedules, numeric
  targets, reminders, and automatic timer-based completion remain outside scope.
  The implementation plan is in [habits-plan.md](habits-plan.md).
  Schema migration 8 adds separate tables; exact request-bound receipts survive
  subsequent edits and restart. Backup, staged v7 restore, JSON export schema 3,
  and authenticated daemon dispatch preserve habits and their history. New
  committed notifications use the actual mutation cursor and timestamp; replay
  emits no duplicate notification. Drawer conflicts retain the draft and offer
  saved-value comparison before retry. Check-in controls retain keyboard focus,
  and sidebar navigation scrolls when needed at the minimum window size.
  Verification: full solution build with zero warnings/errors; all 60 tests pass
  (50 application, 10 domain); `git diff --check` is clean. Tests cover embedded
  and daemon behavior, revision conflicts, bounded reads/corrections, lifecycle,
  durable replay, migration, backup/export, staged restore and rejection of newer
  schemas, CLI reads, DST, midnight, and streaks longer than the read window.
  Persisted headless habit interaction checks pass at 1280×820 and 980×640,
  covering create, check/undo, backfill, draft retention, stale-save review, focus,
  Escape, archive/delete/restore, and history preservation. Seeded/empty Habits
  and editor renders plus Today/Settings sidebar smoke captures are under
  `artifacts/habits/{seeded,narrow,empty,empty-narrow}` and were visually inspected.
  VSTest required local socket access outside the sandbox. Native human and
  assistive-technology review remain separate release work.

- The headless CLI now has a dedicated reviewable mass-edit workflow. `task-batch
  plan` resolves explicit IDs or intersecting text, hierarchy, state, priority,
  favorite, and due-date filters into a deterministic preview with exact revisions;
  broad selection requires explicit `all: true`. `task-batch apply` sends that
  unchanged plan through the atomic backend operation for up to 500 tasks. Saved
  operation IDs make response-uncertain retries exact, while stale targets roll the
  entire batch back. Structured JSON output includes the operation and updated
  tasks, and the machine-readable `api` catalog advertises the workflow. Embedded
  integration coverage verifies planning, conflict rollback, selective mutation,
  tags, due dates, favorites, untouched tasks, and receipt replay; authenticated
  daemon coverage exercises the same plan/apply path. Verification: the full
  solution builds with zero warnings or errors, all 50 tests pass, and
  `git diff --check` is clean.
- Tasks supports checkbox selection in List and Board, Select all in view/Ctrl+A,
  Clear/Escape, and a bulk drawer with explicit fields to apply. Priority, due date,
  favorite, completion, project, default activity, tags, title, description, and
  archive state update atomically for up to 500 tasks. Stable selection survives
  refresh and excludes hidden tasks. Conflicts retain the draft and provide a
  latest-revision review; Cancel discards the draft. Completion retains prerequisite
  rules and permits selected prerequisites to complete in the same transaction.
  Contract 1.3 supports the same batch in embedded and authenticated daemon modes;
  durable receipts replay exact results after subsequent edits or restart.
- Tasks and projects expose star toggles and editor controls; boards do not.
  The Starred scope includes starred tasks and tasks in starred projects. Date-only
  due dates reuse the existing independent field, with a dedicated schema-7
  migration for deadline/favorite indexes. Event Day/Week/Month displays clickable
  due counts for open, unarchived tasks; due-list entries open task details and
  never create schedule blocks. Month event lists remain inside their cells.
  Verification: zero-warning full solution build, all 49 tests passing, and clean
  `git diff --check`. Shared embedded/daemon tests cover atomic rollback, selective
  fields, nullable clears, tags, moves, lifecycle changes, dependency completion,
  bounds, favorites, migration preservation, and restart-safe replay. Persisted
  headless checks cover bulk editing/conflicts/recovery/cancel/focus, selection,
  favorites and due popups at 1280×820 and 980×640. Existing workspace creation,
  drag/drop, task/utility editor, timer, and calendar checks pass at both sizes.
  Seeded and empty images were rendered and inspected under
  `artifacts/task-batch/{seeded,narrow,empty,empty-narrow}`; regression captures are
  in `regression` and `regression-narrow`. VSTest requires loopback socket access
  outside the sandbox. Native human and assistive-technology review remain separate.

- Sidebar navigation replaces all seven font glyphs with original vector icons:
  sun, checklist, stopwatch, calendar, history clock, bar chart, and gear. Shared
  24-pixel slots, rounded strokes, and subtle fills keep detail and weight consistent;
  icons inherit the navigation label's state colors without taking focus or clicks.
  Verification: full solution build with zero warnings/errors, all 47 tests passing,
  and clean `git diff --check`. All seven selected states were rendered and their
  sidebars visually inspected with seeded and empty profiles at 1280×820 and
  980×640 (28 captures in `artifacts/ui-navigation-icons`). VSTest required local
  socket access outside the sandbox. This pass covers the navigation artwork.
- Calendar has Event and History modes. Event retains its existing arrangements;
  History has Week and adaptive Flex, proportional recorded intervals through now,
  gaps for pauses, per-day tracked totals, and gray future task plans clipped at now.
  Flex uses a 156-pixel day-width target and keeps 2–21 days visible. Open intervals
  advance locally; backend timer changes rebuild the view. Overnight splits,
  short records, overlaps, 23/25-hour days, keyboard inspection, and paginated
  history retrieval have dedicated coverage. Calendar events remain in Event mode.
  Verification: full solution build has zero warnings/errors; all 47 tests pass,
  including 14 calendar tests and a 205-session pagination case. Persisted Event
  planning/navigation and History resize, mode memory, keyboard inspection, and
  pause/resume/stop checks pass at 1280×820 and 980×640. Seeded and empty renders
  were inspected in `artifacts/calendar-bimodal/{seeded,narrow,empty,empty-narrow}`.
  `git diff --check` passes. This is headless development verification; native
  human interaction and assistive-technology review remain separate release work.
- Today separates Quick capture and On the horizon with a 14-pixel gap.
  Sidebar icons remain aligned within compact navigation rows.
  Today and Tasks capture pickers group projects under disabled board headings
  and retain board labels on selected choices. Full build and all 33 tests pass;
  persisted capture, board management, drag/drop, and task drawer checks pass.
  Seeded and empty screenshots were inspected at normal and 980×640 sizes.
- Settings hides deleted activities and boards by default with separate Show deleted
  checkboxes. Deleted rows use tinted backgrounds, accent borders, and struck-through
  names alongside their Deleted labels. Tracker suppresses blank description tooltips.
  Full build and all 33 tests passed; persisted headless checks cover hide/show,
  restore, styling, and optional tooltip content at normal and minimum sizes.
  Seeded and empty-profile captures were inspected, including 980×640 layouts.
- Task-sidebar consistency: Tags and visible amber Archive/red Delete actions are
  above Save task, with the immediately committed Move action below. The current
  board/project path updates after moving, and Cancel preserves that move while
  discarding unsaved fields. Move choices have non-selectable board headings;
  prerequisites have board/project headings and contextual labels.
- Task boards use a horizontally scrolling button row with separate arrows.
  Contextual board and project-lane menus expose rename, soft delete, and saved
  earlier/later ordering. Project lanes honor stored order, and board deletion
  hides child lanes until restore. Persisted interaction checks cover menus,
  ordering, recovery, overflow navigation, grouped pickers, and move/cancel behavior.
  Seeded/empty layouts were rendered and inspected at 1280×820 and 980×640;
  interaction checks passed at both sizes, including duplicate project/task names
  across boards and capture selection after rename. Full build and all 33 tests
  passed. Evidence is in `artifacts/ui-task-actions*`.
- Quick task capture accepts Enter on Dashboard and Tasks, using the same guarded
  create command as the button. Tasks now renders board selection independently
  of project lanes: creating a board selects it, shows an empty-board prompt,
  and renders its first project as soon as it is added. All boards remains
  available; selecting a board filters both List and Board views.
  Verified with persisted keyboard/creation/drag/editor checks at 1280×820 and
  980×640, inspected seeded and empty captures, a clean full build, and 33 passing
  domain/application tests. Captures are in `artifacts/ui-capture-board-fix*`.
- Desktop modernization after an intensive rendered/code review: Settings categories
  and readable catalogs replace page-wide maintenance expanders; Tasks has compact
  rows, completion controls, inline board actions, and a filter popup; Tracker has
  toolbar creation/manual-time actions and a route to its activity library; History
  uses a ledger and correction drawer; Summary has selected groupings and proportional
  time bars. Dashboard composition and Calendar's time grid are preserved, with
  calendar creation/editing moved into focused drawers. The written review and plan
  are in `docs/ui-modernization.md`.
- Shared utility editors use compiled bindings, isolated record/picker drafts,
  fixed Save/Cancel areas, scrolling forms, keyboard focus containment/return, and
  actionable revision-conflict feedback. Project/activity tags remain explicit
  immediate actions inside the editor. Notes-only history corrections preserve
  precise interval timestamps; unsaved timer preferences survive refresh and can
  be reset to their saved value.

- Refined light palette: warm neutral surfaces, dark slate text, and deep teal actions; shared Fluent resources align dropdowns, text entry, menus, and interaction states with the workspace. The light variant is explicit rather than mixing system-dark controls with light page surfaces.

- Board/editor redesign pass: shared task drawer for board and list, preserved drafts across backend refreshes, revision-safe saves, visible conflict feedback, Tab containment and Escape cancellation. The board now supports edge scrolling while dragging, project jumps, compact capture controls, and card lift feedback. Settings maintenance is grouped into disclosures, with wrapping project/activity rows. Search labels and visibility match the current workspace.

- Calendar/Today redesign pass: date navigation, duration-based day/week schedules, overlap columns, all-day rows, overnight clipping, current-time indication, selectable month entries and a keyboard-accessible inspector. Users explicitly choose a task/calendar/time when planning, and can start a planned task from its inspector. Today puts four next actions directly beneath the current focus session; upcoming items are chronological and past completed time blocks are excluded. Live picker options preserve selections across timer refreshes.

- Workspace redesign: Grouper-inspired grouped activity launchers opposite running/paused/background session cards; independent tracker pane scrolling; a persistent sidebar focus clock; collapsed maintenance forms; horizontal project lanes with draggable cards, floating previews, highlighted drop targets, and Escape cancellation. Explicit move actions remain available. Today includes upcoming plans beside current work. Data operations live in Settings, and operation status stays visible below the page. The requested redesign is implemented and audited; see `docs/ui-design.md` for direction and evidence.

- Workspace hierarchy: boards, projects, activities, activity groups, and tasks.
- Task lifecycle: edit (including descriptions), complete/reopen, archive/restore, soft-delete/restore, search, filters, sorting, starring, deadlines, tags, links, dependencies, and task details.
- Time tracking: start, pause, resume, stop, manual entries, corrections with provenance, foreground/background lanes, concurrency settings, and restart recovery.
- Calendar: schedule blocks plus calendar events with descriptions, locations, colors, all-day support, bounded daily/weekly recurrence, recurring occurrence exceptions, visibility-aware projections, and inline event move/resize/edit controls.
- Calendar desktop workflow: event and planned-block creation now use an explicit visible-calendar selector and preserve that selection across refreshes; Day, Week, and Month render distinct date-grid layouts while Agenda remains the editable chronological view.
- Today/review: priority and active work, recent and upcoming work, history search with pagination, and summaries by day/task/project/activity/activity group/tag/lane.
- Data operations: migrations, integrity-checked backups, safe restore, JSON export, and CSV worklog export.
- Hosting: embedded SQLite desktop mode, authenticated loopback daemon mode with RPC and SSE changes, and a complete JSON CLI surface generated from the shared backend contract. The CLI supports both host modes, agent-safe named JSON arguments, operation/revision retries, and all workspace, tracking, calendar, recovery, export, backup, and restore methods; see `docs/cli.md`.
- Operations: README guidance, backup/recovery/hosting documentation, and a hardened Linux systemd service definition.
- Desktop finishing: live pickers exclude deleted records; Tasks can reveal and restore deleted tasks; Settings exposes soft-delete/restore for boards, projects, activities, groups, and calendars; deleted calendar events have a restore surface; Today now has both a fast 30-minute manual-time shortcut and an explicit task/activity, UTC start/end, and notes form.
- Ownership recovery: a dead owner PID no longer strands a workspace after an interrupted host, while a live owner still blocks a second host before writes.
- Daemon operations: readiness is emitted only after the loopback listener binds; SIGTERM (including systemd stop) now performs graceful shutdown and releases the workspace lease.
- Capture and tracking workflows: Tasks now creates boards, projects, and tasks in place; Time Tracker is a dedicated sidebar workspace for standalone activity creation, editing, lifecycle management, manual entries, and timer controls; Today can select and switch activities while keeping paused foreground sessions resumable.

## Verification completed

- Modernization: full solution build with zero warnings/errors and all 33 tests
  passing (28 application, 5 domain). VSTest required loopback access outside the
  filesystem sandbox. Seeded and empty captures at 1280×820 and 980×640 were
  rendered and visually reviewed, including catalogs, sparse states, drawers,
  bottom-of-page reachability, and Calendar's Agenda. Artifacts are in
  `artifacts/ui-modernized`, `ui-modernized-narrow`, `ui-modernized-empty`, and
  `ui-modernized-empty-narrow`.
- Combined headless checks passed at standard and minimum sizes: board creation,
  dragging/edge scrolling, task draft and stale-save handling, timers, calendar
  navigation/planning, manual time, activity creation, History validation/provenance
  and exact timestamp preservation, Settings categories, board/activity/calendar
  editing, focus containment/return, and cancellation. Further minimum-size checks
  cover timer-preference drafts/reset, immediate tags without draft loss, and
  calendar soft-delete/restore through the contextual menu. Interaction images are
  in `artifacts/ui-modernized-interactions` and `ui-modernized-interactions-narrow`.
  These are development checks; native human and assistive-technology review remain
  separate release work.

- Dashboard alignment and scrollbar refinement: the lower Today cards now share the upper row's column proportions. Scrollbars use muted gray-green thumbs, neutral tracks, and darker hover/pressed feedback. Verified with a clean solution build, all 33 tests, and persisted board/drawer/timer/calendar interaction checks. Seeded and empty dashboard captures were inspected at 1280×820 and 980×640, alongside tracker and board scrollbars (`/tmp/snook-alignment-images`).

- Final redesign audit: clean solution build, all 30 tests passing, and combined board/editor/tracker/calendar headless interaction checks passing. Daily totals advance with running sessions and refresh across midnight; domain tests cover short/long DST days. Cancel/reopen tests confirm drafts are discarded. Standard, minimum-size, empty, and stress profiles were rendered and inspected. This completes the requested development redesign, not the release/accessibility matrix.

- Board/editor pass: headless checks verify offscreen project moves in a seven-project fixture, project jumping, task draft survival, SQLite saves, stale revision rejection without draft loss, and keyboard focus containment. Narrow task drawers, Settings maintenance, and History correction forms were rendered and inspected.

- Calendar/Today pass: the headless interaction runner verifies day/week/month navigation, keyboard schedule inspection, overlap bounds, planned-task starts, invalid interval rejection, explicit planning persisted to SQLite, and picker selection preservation. All standard screenshot sections were generated, plus stress-calendar, empty, and minimum-size profiles.

- Redesign pass (2026-09-12): the full solution builds with zero warnings/errors and all 27 existing tests pass. The screenshot runner additionally verifies pointer-driven task moves against SQLite, target feedback cleanup, Escape cancellation, and timer pause/resume/stop/activity-start controls. Seeded, empty, and 980×640 captures were rendered and inspected. These checks cover the implemented tracker/board work, not completion of the full redesign or a release accessibility audit.

- Full solution build has passed with zero warnings and zero errors.
- The application suite has 25 passing tests and the domain suite has 5 passing tests, covering timer lifecycle and ownership, standalone activity switching, stale-lease recovery, migrations, recurrence including DST gap/fold behavior and exceptions, calendar event editing and visibility, task details, settings, corrections, backups/restores, ordering, and local-day boundaries.
- CLI integration checks cover generated full-contract discovery, embedded named-argument task mutation plus idempotent retry, and authenticated daemon-mode mutation without opening SQLite. The command reference documents agent workflows, mutation concurrency, calendar/time tracking, maintenance, and transport configuration.
- The daemon-backed contract test covers authenticated access, capability reporting, board creation, and committed change notification. Interrupted SSE responses are treated as reconnectable shutdown/transport events.
- Daemon RPC request bodies and argument counts are bounded before JSON dispatch; the integration test verifies oversized authenticated calls fail closed.
- Task detail durations are presented as readable elapsed labels, and primary desktop controls expose non-visual automation names.
- The redesigned UI has been regenerated through the headless screenshot harness across Today, Tasks, Calendar (including the date-grid modes), History, Summary, and Settings; the harness now builds explicitly and executes the output DLL for reproducibility.
- Desktop visual iteration uses a disposable, lightly seeded screenshot profile. Dropdown labels, task metadata bindings, section/view selection, wrapping board cards, and expandable task/history/calendar editors have been reviewed through regenerated screenshots. The harness also captures lower Today/Settings content and expanded editors; this is not a release accessibility audit.
- Release build and tests are green after the desktop finishing pass; the Release app is started against `artifacts/ui-screenshot-data` for human review.

## Known remaining release scope

- Android packaging and mobile UI are not implemented.
- Peer/cloud synchronization is not implemented; the daemon is a local authenticated transport, not a sync service.
- Windows service packaging is not implemented.
- The accessibility, packaging, and release-readiness matrix still needs a dedicated pass.
- The UI has had build and screenshot review; human interaction and accessibility review remain necessary.

## Useful commands

Build:

```bash
dotnet build Snook.slnx --no-restore -p:MSBuildEnableWorkloadResolver=false -m:1 -nr:false -v:minimal
```

Test:

```bash
dotnet test Snook.slnx --no-build -p:MSBuildEnableWorkloadResolver=false -m:1 -nr:false -v:minimal
```

Launch the desktop app in embedded mode using a disposable data directory:

```bash
SNOOK_DATA_DIR=/tmp/snook-ui-review dotnet run --project src/Snook.Desktop/Snook.Desktop.csproj --no-build
```
