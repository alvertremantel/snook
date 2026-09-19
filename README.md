# Snook

Snook is being built as a local-first, data-private task and time system for
Windows, Linux, and Android. The new implementation will use C#/.NET and
Avalonia UI, with the same application contract available in embedded mode or
through an optional headless daemon.

## Status

The current desktop slice is an Avalonia 12.1 shell backed by a local SQLite
store, with board/project/activity/group organization, in-context
board/project/task capture, task editing and
sorting, archive and soft-delete lifecycles, task dependencies and tags,
attributed timers, a dedicated standalone-activity time tracker, dashboard
activity switching, pause/resume/stop lifecycle, durable interval history,
corrections with provenance, paginated searchable history, civil-day summaries,
bounded recurring schedule blocks, user calendars and recurring events with
occurrence exceptions, operation idempotency, integrity-checked backup,
JSON/CSV export, and in-memory timer display. A structured-JSON CLI and an
authenticated loopback daemon use the same backend contract.
Journals add a separate space for writing, with multiple journals, entry tags,
optional mood ratings from 1 to 7, and complete CLI read/write access.

Android packaging, authenticated peer sync, Windows service packaging, and the
remaining release-gate/accessibility matrix are still later milestones.

Start with the [Snook specification](.opencode/artifacts/specs/spec-2026-09-08-snook/README.md).

## Desktop workspace

- **Quick capture:** type a task title and press Enter (or click Add task).
  The field clears after saving and stays ready for the next task.
- **Tasks:** use compact List or Board views, the completion circle, Start, and
  Edit. Completed, archived, and deleted tasks are available through **Filters**.
  Board/project creation stays in the toolbar. The board button row includes empty
  boards and an All boards view; creating a board selects it and shows how to add
  its first project. Use the row's arrows to scroll boards. The selected board's
  **•••** menu and each project lane's **•••** menu provide rename, delete, and
  Move earlier/later actions; your order is saved. Restore deleted boards/projects
  in Settings → Boards & projects by checking **Show deleted boards**. Drag tasks between projects or use the task
  editor's explicit Move action.
- **Bulk editing:** use the selection checkboxes in List or Board, or **Select all
  in view** (Ctrl+A outside text fields), then **Edit selected**. Check only fields
  you want to apply: priority, due date, favorite, completion, project, activity,
  tags, title, description, and archive state. A blank checked due date clears it;
  **No activity** clears the default activity. Tags can be added or removed without
  replacing other tags. Batches are limited to 500 tasks and commit together. A
  conflict keeps the draft; expand **Review selected tasks**, load the latest
  revisions, review, and apply again. Cancel/Escape discards the draft. Selection
  survives refresh and is restricted to the current filtered view; Escape also
  clears selection outside an editor.
- **Favorites:** toggle the star on task rows/cards and project lane headings, or
  use the task/project editor. **Starred** shows individually starred tasks plus
  tasks in starred projects, with favorite projects listed above. Board and search
  filters still apply. Boards do not have favorites.
- **Due dates:** enter an optional `YYYY-MM-DD` date in the task editor or bulk
  editor. Due dates already have their own date-only database field and remain
  independent of scheduling. Day, Week, and Month show **N due** above days with
  open, unarchived tasks due. Click it to inspect the list and open a task. Only
  explicitly scheduled tasks occupy calendar time; completing or archiving a
  task removes it from due counts without removing its planned blocks.
- **Task details:** Tags and the visible amber Archive/red Delete actions sit
  above Save task; Move sits below it. Save applies the editable task fields.
  Move commits immediately and survives Cancel. Project choices are grouped by
  board, and prerequisites by board/project, so repeated names have context.
- **Time Tracker:** launch grouped activities alongside running, paused, and
  background timers. **Manual time** and **New activity** open focused editors;
  **Activity library** opens their organization settings.
- **Restored timers:** Dashboard recovery cards freeze running timers at the
  backup boundary. **Last known** adds no time; **Stop now** explicitly credits
  the gap; **Continue** starts now without the gap. Both the restore preparation
  and decision retain before/after provenance. See [restore operations](docs/operations.md)
  for clock rollback and retry behavior.
- **Habits:** create a daily yes/no routine with a name, optional description,
  and start date. Click a day to check it off or undo it. The page shows the last
  seven days, an expandable 30-day history, a current streak, and completed days
  in the displayed range. **History through** opens older dates; **Today** returns
  to the present. You can correct the last 366 days, starting no earlier than the
  habit's start date. An unfinished today does not break yesterday's streak until
  the day ends. Each habit retains the local time zone in which it was created.
  Use **•••** to archive/delete, and **Show archived/deleted** to restore with
  history intact. Habits are independent of tasks, timers, and calendar plans.
  This first version supports up to 500 habits including deleted records; weekday
  schedules, numeric targets, reminders, and automatic check-ins are outside scope.
- **Journals:** create a journal, then **Write an entry**. Add an optional title,
  date/time, journal-only tags, and mood from **1 · Very low** to **7 · Great**;
  **Not rated** leaves mood unset. Select a journal or browse all journals, search
  entry text, or apply an exact tag filter. **Read & edit** opens the full entry;
  changing its journal moves it when saved. Tags and mood are saved with the draft.
  **Journal settings** provides rename and delete; **Show deleted** reveals journals
  and entries for reading/restoring. Deleting a journal preserves its entries.
  Restore the journal before restoring individually deleted entries. Conflicts
  keep the draft and offer **Review latest saved entry/journal** before retrying.
  Journals support plain text, up to 20,000 characters per entry, 20 tags per entry,
  and 200 journals including deleted ones. Journal tags never enter task, project,
  activity tags, or time summaries.
- **History:** scan sessions, start times, duration, and state in a ledger. **Edit**
  opens a correction drawer with a required reason and retained provenance.
- **Summary:** choose a grouping to compare attributed time and clock coverage.
  Bars compare attributed duration with the largest group; overlapping sessions
  are counted once per group in clock coverage.
- **Settings:** switch between **General & data**, **Boards & projects**,
  **Activities**, and **Calendars**. Edit a record from its row; the **•••** menu
  contains ordering, archive, delete, and restore actions where supported. Project
  and activity editors also provide an explicitly labeled immediate Add tag action.
- **Calendar:** use **Plan a task** or **New event** above the time grid. The
  **Agenda** view provides event and planned-block editing.

Deleted activities are hidden in Settings → Activities until **Show deleted activities**
is checked. Both deleted activity and board rows use a tinted background and
struck-through name; their actions menu offers **Restore deleted**. Time Tracker
only shows description tooltips for activities with a nonblank description.

Editors retain drafts across workspace refreshes. Save commits the edit; Cancel
or Escape discards it; completed immediate actions (including tags and moves) remain.
Conflicting saves keep the draft visible with recovery
instructions. Times are entered in local time as `YYYY-MM-DD HH:MM`.

## Repository layout

- `Snook.slnx` — .NET solution.
- `src/Snook.Domain/` — time and aggregate types with no I/O dependencies.
- `src/Snook.Application/` — versioned backend seam and use-case orchestration.
- `src/Snook.Persistence.Sqlite/` — SQLite schema, ownership lease, transactions,
  mutation receipts, backup, and export.
- `src/Snook.UI/` — shared Avalonia MVVM shell and dashboard surface.
- `src/Snook.Desktop/` — Windows/Linux desktop composition root.
- `src/Snook.Screenshot/` — headless Avalonia screenshot runner for UI review.
- `src/Snook.Cli/` — structured-JSON CLI composition root over the same backend.
- `src/Snook.Daemon/` — loopback-only daemon host with authenticated RPC and push changes.
- `tests/` — domain time math and application lifecycle coverage.
- `docs/operations.md` — ownership, daemon, backup, restore, migration, and recovery procedures.
- `docs/production-readiness.md` — implemented hardening, current evidence, and
  the remaining production release gates.
- `deploy/systemd/snookd.service` — hardened Linux service template.
- `.opencode/artifacts/specs/` — product and engineering specifications.
- `refs/grouper-main.zip` — immutable legacy reference used for behavior and migration analysis.
- `docs/` — future user-facing and contributor documentation.

## Toolchain baseline

- .NET 10 LTS / C# 14
- Avalonia 12.1.2 (centrally pinned in `Directory.Packages.props`)
- SQLite through `Microsoft.Data.Sqlite`

The Android workload is intentionally not installed in this desktop slice.

## Run locally

```bash
dotnet restore Snook.slnx
dotnet run --project src/Snook.Desktop/Snook.Desktop.csproj

# Structured JSON client
dotnet run --project src/Snook.Cli/Snook.Cli.csproj -- bootstrap
dotnet run --project src/Snook.Cli/Snook.Cli.csproj -- summary day 30
dotnet run --project src/Snook.Cli/Snook.Cli.csproj -- habits

# Optional daemon profile (the client never opens SQLite in this mode)
dotnet run --project src/Snook.Daemon/Snook.Daemon.csproj
SNOOK_HOST_MODE=daemon dotnet run --project src/Snook.Desktop/Snook.Desktop.csproj
```

To run the GUI and CLI together against one daemon, use the same data directory
and endpoint for all three processes. Stop any embedded GUI before starting the
daemon on its workspace. For a disposable development profile:

```bash
# Terminal 1; leave running
SNOOK_DATA_DIR=/tmp/snook-daemon-demo dotnet run --project src/Snook.Daemon --no-build
# Terminal 2
dotnet run --project src/Snook.Desktop --no-build -- --host daemon --data-dir /tmp/snook-daemon-demo
# Terminal 3
dotnet run --project src/Snook.Cli --no-build -- --host daemon --data-dir /tmp/snook-daemon-demo doctor
dotnet run --project src/Snook.Cli --no-build -- --host daemon --data-dir /tmp/snook-daemon-demo watch
```

Both clients accept `--host daemon`, `--data-dir`, `--endpoint`, and
`--token-file`. Their environment equivalents are `SNOOK_HOST_MODE`,
`SNOOK_DATA_DIR`, `SNOOK_DAEMON_ENDPOINT`, and `SNOOK_DAEMON_TOKEN_FILE`.
The default endpoint is `http://127.0.0.1:43871/`; `SNOOK_DAEMON_PORT` changes
the default for both hosts and clients. Once listening, the daemon publishes a
private `Snook/daemon.endpoint.json`; both clients discover its custom port from
the selected data directory unless an endpoint or port was explicitly configured.
The daemon also accepts `--data-dir PATH`, `--port PORT`, and `--help`.
Connections accept HTTP loopback URLs
only. Tokens default to `<data-dir>/Snook/daemon.token`. GUI `--help` lists its
connection options without opening a window or database. See
[daemon operations](docs/operations.md#daemon-profile) for failures, retries,
permissions and token rotation, and [the daemon plan](docs/daemon-plan.md) for
remaining production verification work.

For everyday launching, open **Connection…** in the desktop footer and save a
daemon profile for the next launch, or run `snook --configure` to configure before
opening a workspace. At startup, check **Remember these settings** before Connect.
Both GUI and CLI read `<launch-data-dir>/Snook/client-profile.json`; that launch
directory comes from `--data-dir`, `SNOOK_DATA_DIR`, or the platform default before
reading saved settings. Flags override environment values, which override the saved
profile. It saves mode, data directory, optional endpoint and token-file path—not
credentials. Invalid profiles fail closed. `--no-profile --host daemon` explicitly
bypasses a bad profile; CLI requires a host selection with `--no-profile`.
GUI startup failures keep the settings and a visible Retry action. Changes made
from the running workspace apply on the next launch and do not discard drafts or
switch an active workspace. An outage shows a stale-data warning and Retry refresh;
there is no offline write queue or automatic embedded fallback.

Contract 1.6 supports caller-owned operation IDs for entity create/add actions.
The GUI preserves an unchanged creation attempt after an uncertain response;
CLI automation should supply and retain `request.operationId` and
`request.clientDeviceId` before sending. See [safe create retries](docs/cli.md#calling-every-capability).
Upgrade clients and daemon together. Closing a GUI draft discards its local retry
state, not a write already committed by the daemon.

Schema 10 adds durable exact-result receipts for the older task, organization,
timer, calendar, settings and batch operations. Unchanged operation-ID retries
return their original results after later edits or restart; changed request
payloads are rejected. Older unverifiable receipts require reviewing current
data before issuing a new operation. Back up before upgrading and see
[migration and recovery](docs/operations.md#migration-and-recovery-failures).

## Headless CLI

`snook-cli` is a JSON-in/JSON-out client intended for scripts and autonomous agents.
It exposes the entire versioned `IBackendClient` contract—not a smaller CLI-only
data model—so boards, projects, tasks, activities, timer sessions, calendar
events and exceptions, history, summaries, backups, exports, and recovery all
have the same behavior as the desktop application. Read results are written to
standard output; failures are structured JSON on standard error and have a
non-zero exit code.

Backup and export destinations must be absolute, unused file paths on the host.
Existing files and backup companions are never overwritten; workspace/credential
names and symbolic-link paths are rejected. Choose a fresh name for each run.
See [safe artifact destinations](docs/operations.md#backup-and-restore).
The [JSON export format](docs/json-export.md) is schema 5, including persisted
settings, task links/dependencies and stored deletion history alongside the
existing task, timer, calendar, habit and journal data.
Restore requires the matching format-1 `.manifest.json` and verifies the staged
backup's size, SHA-256, SQLite integrity and schema before activation. Keep each
backup with its manifest; bare database copies are not accepted by normal restore.

```bash
# Discover every available operation, named argument, type, and default.
dotnet run --project src/Snook.Cli/Snook.Cli.csproj -- api

# Work with a disposable embedded workspace.
dotnet run --project src/Snook.Cli/Snook.Cli.csproj -- --data-dir /tmp/snook-agent bootstrap
dotnet run --project src/Snook.Cli/Snook.Cli.csproj -- --data-dir /tmp/snook-agent \
  call create-task '{"projectId":"<id>","title":"Prepare release notes","priority":"High"}'

# Invoke concise read helpers.
dotnet run --project src/Snook.Cli/Snook.Cli.csproj -- --data-dir /tmp/snook-agent tasks release
dotnet run --project src/Snook.Cli/Snook.Cli.csproj -- --data-dir /tmp/snook-agent summary project 14

# Resolve a safe, revision-pinned mass edit for review, then apply that exact plan.
dotnet run --project src/Snook.Cli/Snook.Cli.csproj -- --data-dir /tmp/snook-agent \
  task-batch plan '{"selection":{"search":"release"},"update":{"priority":"High","starred":true}}' \
  > /tmp/snook-task-plan.json
dotnet run --project src/Snook.Cli/Snook.Cli.csproj -- --data-dir /tmp/snook-agent \
  task-batch apply "$(cat /tmp/snook-task-plan.json)"
```

Use `call METHOD JSON_OBJECT` for every backend method. Method names are
case-insensitive; hyphens and the `Async` suffix are optional. Arguments are
always named JSON properties, which avoids positional ambiguity. See
[the CLI reference](docs/cli.md) for lifecycle, timer, calendar, data-operation,
and retry examples, including journal creation, entry updates, mood/tag changes,
delete/restore, and paginated reads. `journals [--include-deleted]` lists journals;
`journal-entries [JournalEntryQuery JSON]` reads entries. All journal writes use
`call` with an operation ID and, for edits, the current revision.

`task-batch plan` supports explicit task IDs or intersecting search, board, project,
status, priority, favorite, and due-date filters. It makes no changes and emits the
exact task revisions, patch, preview, and operation ID. `task-batch apply` commits
that saved plan atomically for 1–500 tasks; stale revisions change nothing, and an
exact retry returns the first committed result. Empty selections require an explicit
`all: true`. See the CLI reference for every selector and nullable-field syntax.

Use `watch` when an agent needs committed change notifications. It emits NDJSON:
one `ready` record plus `change` records, and runs until cancelled. It is
particularly useful in daemon mode, where it uses the authenticated change
stream rather than opening the database.

The transport is configurable now, even though a future database server is not
part of this desktop slice. `--host embedded` (the default) owns the SQLite
workspace; `--host daemon` creates a `DaemonBackendClient` and never opens the
database. Daemon settings can also use `SNOOK_HOST_MODE`,
`SNOOK_DAEMON_ENDPOINT`, `SNOOK_DAEMON_TOKEN`, and `SNOOK_DAEMON_PORT`.

```bash
dotnet run --project src/Snook.Cli/Snook.Cli.csproj -- \
  --data-dir /var/lib/snook-cli --host daemon \
  --endpoint http://127.0.0.1:43871/ --token-file /var/lib/snook/Snook/daemon.token \
  doctor
```

Never place a daemon token in a command history when `--token-file` or the
private default token file can be used. The CLI fails explicitly if daemon
credentials or connectivity are unavailable; it does not fall back to embedded
SQLite.

### Fedora RPM

The RPM contains self-contained `linux-x64` GUI, CLI and daemon builds. Install the packaging
tool once, then build and install it with:

```bash
sudo dnf install rpm-build
./scripts/package-rpm.sh --install
```

The package preserves `/usr/bin/snook` and the desktop launcher as the GUI.
It also installs `/usr/bin/snook-cli`, `/usr/bin/snookd`, a per-user
`snookd.service`, and CLI/operations documentation under `/usr/share/doc/snook`.
The three self-contained payloads live separately under `/usr/lib64/snook`.
Native OS dependencies, including ICU, time-zone data and OpenSSL, are declared
by the package; no separate .NET installation is needed. See Microsoft's
[Fedora dependency reference](https://learn.microsoft.com/en-us/dotnet/core/install/linux-fedora#dependencies).
Workspace data remains in the user's local application-data directory. No service
is enabled or started, and no workspace is opened by installation. To opt in,
first close the embedded GUI and stop other owners of that workspace, then run:

```bash
systemctl --user daemon-reload
systemctl --user enable --now snookd.service
snook-cli --host daemon doctor
snook --host daemon
```

Use `snook-cli --host daemon` while the daemon owns the workspace, or save a daemon
connection profile so both normal launchers use it. Without saved settings or
overrides, embedded remains the default. The desktop menu also offers **Connect to
local daemon** and **Connection settings** actions. See
[service operation](docs/operations.md#linux-service) for custom profiles, port
conflicts, startup checks, upgrades and stopping.

The packaging script uses a fresh subdirectory of `SNOOK_RPM_WORK_DIR` (default
`.rpm-work`), never deletes an existing build directory, and installs only the RPM
just built when `--install` is selected. `SNOOK_RPM_VERSION` accepts a numeric
major.minor.patch version. Build directories remain available for inspection.
Verify a trusted, locally built RPM without installing it:

```bash
./scripts/verify-rpm.sh artifacts/rpm/snook-0.1.0-2.fc44.x86_64.rpm
```

Use the actual filename emitted by your build. The verifier requires `rpm`,
`rpm2cpio`, `cpio`, Python 3 and local socket/process access. It checks installed
paths/launchers and runs the extracted payloads against fresh disposable data,
including daemon discovery, CLI writes/watch, failed ownership, shutdown and
embedded reopening. It leaves its temporary directory for inspection and does
not install the package, render the GUI, or validate a live systemd service.
To additionally test managed operation when a user systemd manager is available:

```bash
./scripts/verify-user-service.sh /tmp/snook-rpm-check.REPLACE_WITH_PRINTED_DIRECTORY
```

This imports the extracted unit's properties into a uniquely named transient user
unit with a fresh workspace and port. It checks readiness, restart, stop and lease
reopening, then stops/removes the temporary unit. It never enables the normal
`snookd.service`; it is not an install/upgrade/uninstall test.

If `rpmbuild` cannot be installed,
`scripts/package-rpm.sh` is also the reproducible installation script to run
after installing `rpm-build` on a Fedora build host.

The desktop profile stores its workspace at the platform-private local
application-data path under `Snook/workspace.db`. Set `SNOOK_DATA_DIR` to an
explicit directory for a disposable development profile.

## Calendar modes

Calendar has two modes. **Event** keeps the existing Day, Week, Month, and Agenda
arrangements for scheduled tasks and events. **History** offers Week and Flex:
recorded task and activity intervals fill their actual time, pauses remain gaps,
and future task plans appear in gray. Plans crossing the present are clipped at
now; past plans and calendar events do not appear in History. Open intervals grow
locally every 15 seconds, with timer changes refreshed from the backend.

Flex fits days across the available width using a 156-pixel minimum day width,
within a 2–21 day limit. It centers the selected date, and previous/next moves by
the displayed day count. Each mode remembers its arrangement for the current app
session. Daily tracked totals sum recorded intervals, including simultaneous
sessions. Select a time mark with the pointer or keyboard for exact timestamps,
duration, activity, and notes. Very short sessions remain thin marks instead of
inflated cards. On clock-change days, History preserves real elapsed durations
and labels that day's local hours and UTC offsets separately.

## UI screenshots

Build the solution, then run `scripts/capture-ui-screenshots.sh` to render the
main UI screens into `artifacts/ui-screenshots/`. The harness uses a disposable
SQLite profile in `artifacts/ui-screenshot-data/` and captures Today, Tasks
(list and board), Time Tracker, Calendar (Event and History modes), History, Summary, and
Settings at 1280×820. Set `SNOOK_SCREENSHOT_SECTIONS` to a comma-separated
subset such as `tasks-list,settings` when iterating on a smaller area.
The default run produces 29 images, including Journals (`journals`, `journals-editor`,
`journals-new`), Habits (`habits`, `habits-editor`), task and utility drawers
(`tasks-details`, `tracker-new-activity`, `tracker-manual`, `history-details`,
`calendar-details`), the lower Today section (`today-bottom`), and Settings
categories (`settings-organization`, `settings-activities`,
`settings-activities-bottom`, `settings-calendars`, `settings-activity-editor`).
`calendar-plan` and `settings-board-editor` provide additional drawer captures.
`calendar-history-week` and `calendar-history-flex` capture calendar History.
With `SNOOK_SCREENSHOT_VERIFY_INTERACTIONS=1`, `calendar-history-flex` checks
persisted recorded intervals, the past/future boundary, keyboard inspection,
resizing, date navigation, each mode's remembered arrangement, and timer
pause/resume/stop refreshes.
An empty screenshot profile is seeded with three sample tasks, a 45-minute
session, a planned block, two events, grouped activities, and running/paused/background
timers. The fixture also includes two journals and three tagged entries with mood
ratings. Existing tasks prevent repeat seeding; set `SNOOK_SCREENSHOT_SEED=0`
to capture an empty profile without sample data.
This data is confined to the screenshot profile, not the desktop workspace.

Set `SNOOK_HOST_MODE=daemon` to capture against a separately started disposable
daemon instead of embedded SQLite. The same data-directory/endpoint/token-file
settings apply. With `SNOOK_SCREENSHOT_VERIFY_CONNECTION=1`, the daemon capture
first checks a visible missing-token failure, edits the token-file path, saves a
profile and retries through the actual connection window. A separate client-only
directory must remain free of SQLite. On the `today` section, both host modes also
check settings validation, retained drafts, Escape, focus return and next-launch
save without switching the active backend. Dialogs are captured at 620×600; use
`SNOOK_SCREENSHOT_SIZE=980x640` for the workspace. Existing workspace/timer checks
can run against this same daemon with their usual verification switches.
For an externally controlled shutdown/restart check, also set
`SNOOK_SCREENSHOT_VERIFY_OUTAGE=1`. The harness prints `WAIT` before each phase:
stop that disposable daemon, then restart it with the same data root and port.
Each phase has a 60-second limit. It captures the stale-data warning and recovery,
clicks Retry refresh while unavailable and checks that a typed draft survives.
Never use this switch against a normal user service/workspace.

`SNOOK_SCREENSHOT_RESTORE_RECOVERY=1` creates a verified backup and restores it in
the disposable screenshot profile, producing at least three recovery cards.
Use a fresh profile, `SNOOK_SCREENSHOT_SECTIONS=today,tracker,history` and
`SNOOK_SCREENSHOT_SIZE=980x640`. Add `SNOOK_SCREENSHOT_VERIFY_RECOVERY=1` to click
all three recovery choices, check their persisted transitions/provenance, preserve
focus across refresh, and capture the reachable actions and resolved state.
Capture normal seeded and empty states separately without this fixture switch.

`SNOOK_SCREENSHOT_VERIFY_JOURNALS=1` on the `journals` section exercises persisted
journal creation/rename, entry creation/edit/movement, filters, mood and tag
clearing, delete/restore, draft retention, stale-save review, focus containment and
return, and Cancel/Escape. Run it at both the default and 980×640 sizes.

For an isolated review, set `SNOOK_DATA_DIR` to a fresh disposable directory and
`SNOOK_SCREENSHOT_DIR` to the desired image directory. Set
`SNOOK_SCREENSHOT_SIZE=980x640` to review the minimum window size. With a seeded
disposable profile, `SNOOK_SCREENSHOT_VERIFY_INTERACTIONS=1` additionally exercises
task dragging, Escape cancellation, and timer pause/resume/stop/start through pointer
input, then checks persisted state. The `calendar-week` section also checks date
navigation, keyboard inspection, planned-task starts, and explicit planning. This
option intentionally changes the capture profile. Include `tasks-board,tracker,calendar-week`
to run the existing checks; add `calendar-history-flex` for the new mode checks.
On a fresh profile, `SNOOK_SCREENSHOT_CALENDAR_STRESS=1` adds
overlapping, all-day, and overnight events, plus recorded sessions, short work,
gaps, and past/future plans for calendar layout review.
`SNOOK_SCREENSHOT_BOARD_STRESS=1` adds a wide board and long project lane; the
board interaction checks then include edge scrolling to an offscreen project.
Task-drawer checks cover preserved drafts, saves, keyboard focus, and revision
conflicts. The older `settings-details` / `settings-details-bottom` section names
now select the organization catalog; `tracker-details-bottom` opens new activity
creation. `settings-bottom` captures the lower General & data page.
`SNOOK_SCREENSHOT_VERIFY_PALETTE=1` adds focused-input, open-dropdown, and
hover/pressed-button captures when `tasks-details` is included.
`SNOOK_SCREENSHOT_VERIFY_WORKSPACE=1` checks the dashboard column order, Enter
capture (including blank/duplicate protection and retained focus), and
with `tasks-list`, grouped project choices and persisted capture into the selected
project with board context preserved after refresh. It also
with `tasks-board`, captures and exercises the board/project creation popup
against the disposable profile, including rendered empty boards, their first
project lane, board button selection/scrolling, rename/delete/restore, saved board
and project ordering, and Escape/outside-click dismissal. Task interaction checks
also cover sidebar action order, grouped pickers, and moves retained after Cancel.
`SNOOK_SCREENSHOT_VERIFY_TASK_BATCH=1` with `tasks-list` checks selection, bulk
saves, atomic stale-revision rejection and recovery, Cancel/focus, task/project
favorites, the Starred scope, and calendar due popups against disposable SQLite.
`tasks-starred` and `tasks-bulk` are also available as static screenshot sections.
`SNOOK_SCREENSHOT_VERIFY_HABITS=1` with `habits` checks habit creation, check/undo,
history backfill, archive/delete/restore, retained drafts, stale-save recovery,
Escape, and keyboard focus against disposable SQLite state. `habits-editor`
captures the creation drawer for an empty profile or the first habit's editor
for a seeded profile.
`SNOOK_SCREENSHOT_VERIFY_EDITORS=1` adds persisted checks for the utility editors.
Include `tracker,history,settings`: these verify activity/manual-time creation,
picker preservation across refresh, correction validation and provenance, exact
timestamp preservation, category navigation, board/activity/calendar saves,
keyboard focus, Cancel, and stale revision rejection. Use a fresh seeded profile;
these checks intentionally mutate its disposable data. They also verify deleted
board/activity visibility, checkbox filtering, distinct styling, persistent restore,
and optional tracker tooltips, capturing `settings-deleted-boards` and
`settings-deleted-activities`.

The workspace redesign and its verification are documented in `docs/ui-design.md`.
Backup, export, and restore actions are under Settings → General & data.

## Legacy reference integrity

`refs/grouper-main.zip` SHA-256:

```text
2b26bb9d5fc8262614d50a00d82a7b66af2c050355da3994c2c8f2b24fac46d0
```

Do not develop against the extracted Python source or mutate an original
database in place. Legacy data is handled by a read-only importer defined in
the migration specification.

## Licensing

The legacy archive is AGPL-3.0. Snook is proprietary, closed-source software. All rights are reserved by the
licensor. The legacy archive remains AGPL-3.0 and is used only as a reference;
legacy implementation code must not be copied into Snook without an explicit
license review.
