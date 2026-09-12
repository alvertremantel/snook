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

Android packaging, authenticated peer sync, Windows service packaging, and the
remaining release-gate/accessibility matrix are still later milestones.

Start with the [Snook specification](.opencode/artifacts/specs/spec-2026-09-08-snook/README.md).

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

# Optional daemon profile (the client never opens SQLite in this mode)
dotnet run --project src/Snook.Daemon/Snook.Daemon.csproj
SNOOK_HOST_MODE=daemon dotnet run --project src/Snook.Desktop/Snook.Desktop.csproj
```

### Fedora RPM

The desktop RPM is a self-contained `linux-x64` build. Install the packaging
tool once, then build and install it with:

```bash
sudo dnf install rpm-build
./scripts/package-rpm.sh --install
```

The package installs `/usr/bin/snook` and a desktop launcher. Workspace data
remains in the user's local application-data directory; the RPM does not
enable or install the optional daemon. If `rpmbuild` cannot be installed,
`scripts/package-rpm.sh` is also the reproducible installation script to run
after installing `rpm-build` on a Fedora build host.

The desktop profile stores its workspace at the platform-private local
application-data path under `Snook/workspace.db`. Set `SNOOK_DATA_DIR` to an
explicit directory for a disposable development profile.

## UI screenshots

Build the solution, then run `scripts/capture-ui-screenshots.sh` to render the
main UI screens into `artifacts/ui-screenshots/`. The harness uses a disposable
SQLite profile in `artifacts/ui-screenshot-data/` and captures Today, Tasks,
Time Tracker
(list and board), Calendar (day/week/month/agenda), History, Summary, and
Settings at 1280×820. Set `SNOOK_SCREENSHOT_SECTIONS` to a comma-separated
subset such as `tasks-list,settings` when iterating on a smaller area.
The default run also includes expanded task, activity, history, and calendar
editors (`tasks-details`, `tracker-details-bottom`, `history-details`,
`calendar-details`) and the bottom of Today and Settings (`today-bottom`,
`settings-bottom`), for 17 images total.
An empty screenshot profile is seeded with three sample tasks, a 45-minute
session, a planned block, and two events. Existing tasks prevent repeat seeding;
set `SNOOK_SCREENSHOT_SEED=0` to capture an empty profile without sample data.
This data is confined to the screenshot profile, not the desktop workspace.

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
