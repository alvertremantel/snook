# Snook implementation progress

Updated: 2026-09-09

## Current state

Snook is a working desktop vertical slice for local work organization and time tracking. It builds as a .NET 10 Avalonia application, persists to SQLite, and can optionally use an authenticated local daemon. The UI is ready for a human visual pass; this is not yet a finished multi-platform release.

## Implemented surface

- Workspace hierarchy: boards, projects, activities, activity groups, and tasks.
- Task lifecycle: edit, complete/reopen, archive/restore, soft-delete/restore, search, filters, sorting, starring, deadlines, tags, links, dependencies, and task details.
- Time tracking: start, pause, resume, stop, manual entries, corrections with provenance, foreground/background lanes, concurrency settings, and restart recovery.
- Calendar: schedule blocks plus calendar events with descriptions, locations, colors, all-day support, bounded daily/weekly recurrence, and recurring occurrence exceptions.
- Today/review: priority and active work, recent and upcoming work, history search with pagination, and summaries by day/task/project/activity/activity group/tag/lane.
- Data operations: migrations, integrity-checked backups, safe restore, JSON export, and CSV worklog export.
- Hosting: embedded SQLite desktop mode, authenticated loopback daemon mode with RPC and SSE changes, and CLI bootstrap/summary workflows.
- Operations: README guidance, backup/recovery/hosting documentation, and a hardened Linux systemd service definition.

## Verification completed

- Full solution build has passed with zero warnings and zero errors.
- Application and domain tests have covered timer lifecycle, organization lifecycle, migrations, recurrence and exceptions, task details, settings, corrections, backups/restores, and ordering.
- CLI smoke checks have passed for bootstrap and summary against a disposable data directory.
- The daemon-backed contract test now compiles and covers authenticated access, capability reporting, board creation, and committed change notification. It still needs to be executed in the next verification pass.

## Known remaining release scope

- Android packaging and mobile UI are not implemented.
- Peer/cloud synchronization is not implemented; the daemon is a local authenticated transport, not a sync service.
- Windows service packaging is not implemented.
- The accessibility, packaging, and release-readiness matrix still needs a dedicated pass.
- The UI has had functional build verification but still benefits from human visual review and polish feedback.

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
