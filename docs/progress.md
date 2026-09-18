# Snook implementation progress

Updated: 2026-09-18

## Current state

Snook is a working desktop vertical slice for local work organization and time tracking. It builds as a .NET 10 Avalonia application, persists to SQLite, and can optionally use an authenticated local daemon. The UI is ready for a human visual pass; this is not yet a finished multi-platform release.

## Implemented surface

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
