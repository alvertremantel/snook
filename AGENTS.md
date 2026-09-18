# Snook agent guide

## Architecture and boundaries

Snook is a .NET 10/C# 14 Avalonia 12 desktop app with one local SQLite workspace.
Embedded mode is the default; daemon mode is an authenticated loopback transport over
the same backend contract, not synchronization or a second data store.

- `src/Snook.Domain` owns immutable models, validation, recurrence, and time math; keep it
  free of I/O dependencies.
- `src/Snook.Contracts` owns the versioned `IBackendClient`, transport DTOs, operation
  requests, capabilities, and change notifications; it depends only on Domain.
- `src/Snook.Persistence.Sqlite` owns schema/migrations, the single-owner lease,
  transactions, receipts, backup/restore, and export.
- `src/Snook.Application` owns orchestration: `SnookBackend` is the embedded implementation
  and `DaemonBackendClient` is the remote implementation.
- `src/Snook.UI` is the shared Avalonia UI. `src/Snook.Desktop`, `src/Snook.Daemon`,
  `src/Snook.Cli`, and `src/Snook.Screenshot` are the desktop, RPC/SSE,
  structured-JSON, and headless-render composition roots.
- `tests/Snook.Domain.Tests` covers pure time/domain rules;
  `tests/Snook.Application.Tests` covers SQLite, UI projections, CLI, and embedded/
  daemon behavior.

Package versions belong in `Directory.Packages.props`, not individual projects. The
repo builds with nullable analysis, latest recommended analyzers, code-style checks,
and warnings as errors.

## Commands that matter

The SDK is pinned by `global.json`. Restore once (and whenever package inputs change),
then keep the workload resolver disabled and use the restored state:

```bash
dotnet restore Snook.slnx -p:MSBuildEnableWorkloadResolver=false
dotnet build Snook.slnx --no-restore -p:MSBuildEnableWorkloadResolver=false -m:1 -nr:false -v:minimal
dotnet test Snook.slnx --no-build -p:MSBuildEnableWorkloadResolver=false -m:1 -nr:false -v:minimal
git diff --check
```

Run the smallest test project while iterating; filter by class or method for one case:

```bash
dotnet test tests/Snook.Domain.Tests/Snook.Domain.Tests.csproj --no-restore -p:MSBuildEnableWorkloadResolver=false -m:1 -nr:false -v:minimal
dotnet test tests/Snook.Application.Tests/Snook.Application.Tests.csproj --no-restore -p:MSBuildEnableWorkloadResolver=false -m:1 -nr:false -v:minimal --filter 'FullyQualifiedName~Snook.Application.Tests.JournalTests'
```

The application suite launches daemon processes and binds loopback sockets; the test
environment must permit local process/socket access. Always finish code changes with
the full build, full test, and `git diff --check` sequence above.

For disposable embedded UI review after building:

```bash
SNOOK_DATA_DIR=/tmp/opencode/snook-ui-review dotnet run --project src/Snook.Desktop/Snook.Desktop.csproj --no-build
```

For UI changes, capture into fresh disposable paths after restore:

```bash
SNOOK_DATA_DIR=/tmp/opencode/snook-ui-data SNOOK_SCREENSHOT_DIR=/tmp/opencode/snook-ui-images ./scripts/capture-ui-screenshots.sh
```

Use `SNOOK_SCREENSHOT_SECTIONS` to focus the run, capture seeded and empty states in
separate fresh profiles, include `SNOOK_SCREENSHOT_SIZE=980x640` when layout can
change, enable the relevant `SNOOK_SCREENSHOT_VERIFY_*` persisted checks, and inspect
the PNGs. `README.md` lists section names and interaction switches. Never point the
app, tests, or screenshot runner at a real workspace.

## Contract and persistence invariants

- Treat `IBackendClient` as a versioned wire contract. A change must update
  `ContractInfo`, `SnookBackend`, `DaemonBackendClient`, and embedded/daemon contract
  coverage together. Daemon RPC and the CLI `api` catalog derive their callable
  methods from the interface; do not add a separate CLI-only data model.
- Mutations are transactional, idempotent, and revision-checked. Persist stable IDs,
  exact-result receipts, and the mutation-log entry together; notify only after a
  fresh commit, with its actual cursor/time. Exact receipt replay emits no duplicate.
- Applied migrations are append-only and checksummed. Do not edit earlier migration
  SQL: add a new sequence and update initialization, compatibility checks, staged
  restore upgrades, backup/export behavior, and migration/restore tests.
- Preserve soft-delete/restore semantics, exact receipt replay after later edits or
  restart, verified backup integrity, and restore-through-staging behavior.
- Only one process may own a workspace. Preserve fail-before-write for a live owner,
  stale-owner recovery, and graceful daemon shutdown/lease release.
- Daemon mode must fail closed: no fallback to embedded SQLite on missing credentials,
  authentication, connectivity, or protocol errors. Keep the listener loopback-only,
  authenticate health and SSE as well as RPC, keep tokens only in the private token
  file, and never log them.
- Persist and transport explicit UTC instants, but preserve local civil dates, time
  zones, DST gaps/folds, and real elapsed duration. Inject `TimeProvider` into
  testable time-dependent code.
- Bound recurrence expansion, pagination, batch sizes, request bodies, and all other
  caller-controlled work; validate external paths, URIs, IDs, and intervals at the
  contract boundary.

## UI invariants

Follow `docs/ui-design.md`: compact stable navigation, direct task/timer actions,
text as well as color for state, and non-drag alternatives for card movement. An open
editor owns its draft: refresh must not overwrite it; Cancel/Escape discards it; a
revision conflict keeps it visible with actionable recovery. Preserve focus
containment/return, automation names, minimum-size scroll reachability, and visible
hover/focus/pressed/drop states.

AXAML binding mode is intentionally mixed: `MainWindow.axaml` disables compiled
bindings at its root while typed templates and newer views opt in with `x:DataType`.
Do not enable compiled bindings globally without resolving every inherited binding.

## Operational hazards and documentation

- Inspect `git status --short` before editing and never discard unrelated worktree
  changes.
- Treat `refs/grouper-main.zip` as an immutable AGPL behavior/migration reference. Do
  not extract it as a development source, mutate an original legacy database, or copy
  legacy implementation code without explicit license review.
- Update `README.md` for user-visible setup/runtime/screenshot/packaging changes,
  `docs/operations.md` for hosting/ownership/migration/backup/recovery/security,
  `docs/ui-design.md` for interaction direction, and `docs/progress.md` when verified
  implementation or release scope materially changes.
- Do not claim Android/mobile packaging, peer/cloud sync, Windows service packaging,
  native human review, assistive-technology validation, or release readiness unless
  that work was implemented and verified.
- RPM work is Fedora `linux-x64` only; `scripts/package-rpm.sh` requires `rpmbuild`.
  Inspect the built package's installed paths and desktop launcher, not just script
  success.
