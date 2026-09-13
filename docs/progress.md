# Snook implementation progress

Updated: 2026-09-12

## Current state

Snook is a working desktop vertical slice for local work organization and time tracking. It builds as a .NET 10 Avalonia application, persists to SQLite, and can optionally use an authenticated local daemon. The UI is ready for a human visual pass; this is not yet a finished multi-platform release.

## Implemented surface

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
- Hosting: embedded SQLite desktop mode, authenticated loopback daemon mode with RPC and SSE changes, and CLI bootstrap/summary workflows.
- Operations: README guidance, backup/recovery/hosting documentation, and a hardened Linux systemd service definition.
- Desktop finishing: live pickers exclude deleted records; Tasks can reveal and restore deleted tasks; Settings exposes soft-delete/restore for boards, projects, activities, groups, and calendars; deleted calendar events have a restore surface; Today now has both a fast 30-minute manual-time shortcut and an explicit task/activity, UTC start/end, and notes form.
- Ownership recovery: a dead owner PID no longer strands a workspace after an interrupted host, while a live owner still blocks a second host before writes.
- Daemon operations: readiness is emitted only after the loopback listener binds; SIGTERM (including systemd stop) now performs graceful shutdown and releases the workspace lease.
- Capture and tracking workflows: Tasks now creates boards, projects, and tasks in place; Time Tracker is a dedicated sidebar workspace for standalone activity creation, editing, lifecycle management, manual entries, and timer controls; Today can select and switch activities while keeping paused foreground sessions resumable.

## Verification completed

- Final redesign audit: clean solution build, all 30 tests passing, and combined board/editor/tracker/calendar headless interaction checks passing. Daily totals advance with running sessions and refresh across midnight; domain tests cover short/long DST days. Cancel/reopen tests confirm drafts are discarded. Standard, minimum-size, empty, and stress profiles were rendered and inspected. This completes the requested development redesign, not the release/accessibility matrix.

- Board/editor pass: headless checks verify offscreen project moves in a seven-project fixture, project jumping, task draft survival, SQLite saves, stale revision rejection without draft loss, and keyboard focus containment. Narrow task drawers, Settings maintenance, and History correction forms were rendered and inspected.

- Calendar/Today pass: the headless interaction runner verifies day/week/month navigation, keyboard schedule inspection, overlap bounds, planned-task starts, invalid interval rejection, explicit planning persisted to SQLite, and picker selection preservation. All standard screenshot sections were generated, plus stress-calendar, empty, and minimum-size profiles.

- Redesign pass (2026-09-12): the full solution builds with zero warnings/errors and all 27 existing tests pass. The screenshot runner additionally verifies pointer-driven task moves against SQLite, target feedback cleanup, Escape cancellation, and timer pause/resume/stop/activity-start controls. Seeded, empty, and 980×640 captures were rendered and inspected. These checks cover the implemented tracker/board work, not completion of the full redesign or a release accessibility audit.

- Full solution build has passed with zero warnings and zero errors.
- The application suite has 25 passing tests and the domain suite has 5 passing tests, covering timer lifecycle and ownership, standalone activity switching, stale-lease recovery, migrations, recurrence including DST gap/fold behavior and exceptions, calendar event editing and visibility, task details, settings, corrections, backups/restores, ordering, and local-day boundaries.
- CLI smoke checks have passed for bootstrap and summary against a disposable data directory.
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
