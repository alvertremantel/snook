# Snook contributor guide

## Product and architecture

Snook is a local-first task, calendar, and time-tracking desktop application built
with .NET 10, C# 14, Avalonia 12, and SQLite. The supported desktop default is an
embedded SQLite backend. Daemon mode is an explicit, authenticated loopback HTTP
transport over the same backend contract; it is not a sync service.

Keep the dependency direction and host boundary clear:

- `src/Snook.Domain`: immutable records, validation, time math, recurrence, and no
  I/O dependencies.
- `src/Snook.Contracts`: the shared `IBackendClient` surface, DTOs, operation
  requests, capabilities, and change notifications. It depends only on Domain.
- `src/Snook.Persistence.Sqlite`: schema, migrations, ownership lease,
  transactions, mutation receipts, backup/restore, and export.
- `src/Snook.Application`: use-case orchestration. `SnookBackend` is the embedded
  implementation; `DaemonBackendClient` is the remote implementation.
- `src/Snook.UI`: shared Avalonia views, view models, timeline/layout logic, and
  live display clocks.
- `src/Snook.Desktop`: Windows/Linux composition root and embedded/daemon host
  selection.
- `src/Snook.Daemon`: authenticated loopback RPC plus the SSE change stream.
- `src/Snook.Cli`: structured-JSON bootstrap and summary client.
- `src/Snook.Screenshot`: headless UI renderer and persisted interaction checks.
- `tests/Snook.Domain.Tests`: time, recurrence, and civil-day rules.
- `tests/Snook.Application.Tests`: embedded/daemon contract, persistence, and
  lifecycle behavior.

Package versions are centralized in `Directory.Packages.props`. Nullable analysis,
recommended analyzers, code style, and warnings-as-errors are enabled repository-wide.
Follow `.editorconfig`; use file-scoped namespaces and compiled Avalonia bindings.

## Non-negotiable behavior

- Preserve user changes in a dirty worktree. Inspect `git status --short` before
  editing, and never discard unrelated changes.
- Use `apply_patch` for deliberate hand edits; reserve formatters or scripted
  rewrites for clearly mechanical changes.
- Keep mutations transactional, idempotent, and revision-checked. Mutable
  aggregates need stable IDs and revisions; operation IDs and committed change
  notifications must describe successful mutations.
- Keep `SnookBackend` and `DaemonBackendClient` behaviorally equivalent. When
  changing `IBackendClient`, update both clients, daemon dispatch, and application
  contract coverage in the same change.
- Never silently fall back to embedded SQLite when `SNOOK_HOST_MODE=daemon`.
  Authentication, connectivity, and protocol failures must be explicit, and a
  daemon client must not open the workspace database directly.
- Allow only one SQLite owner. Preserve the lease, stale-owner recovery, graceful
  daemon shutdown, and fail-before-write behavior for a live competing owner.
- Treat soft delete/restore, migration compatibility, backup integrity, safe
  restore, and mutation receipt replay as first-class paths rather than edge cases.
- Bound recurrence expansion, history pagination, RPC request sizes, and other
  caller-controlled work. Validate external paths, URIs, intervals, and IDs at the
  boundary.
- Use explicit UTC instants for persistence and transport while preserving correct
  local civil-day and DST behavior in projections and summaries. Prefer `TimeProvider`
  in testable time-dependent code.
- Keep daemon tokens only in the private token file. Bind the daemon to loopback,
  require authentication even for health checks, and never log secrets.
- Do not mutate an original legacy database or develop against an extracted copy of
  `refs/grouper-main.zip`. The archive is an immutable behavior/migration reference;
  do not copy AGPL implementation code into Snook without an explicit license review.

## UI changes

Preserve the workspace direction in `docs/ui-design.md`: compact stable navigation,
direct task and timer actions, distinct running/paused/background states, and a
time-based calendar that connects planned work to execution. Keep equivalent
non-drag actions for card movement and communicate state with text as well as color.

An open editor owns its draft. Backend refreshes must not overwrite it; Cancel and
Escape discard it; stale revisions keep the draft visible with actionable feedback.
Maintain keyboard focus containment/return, automation names, minimum-window
behavior, scroll reachability, and visible hover/focus/pressed/drop states.

For UI work, use `src/Snook.Screenshot` in addition to tests. Render the affected
seeded and empty screens, include the 980x640 minimum size when layout can change,
and inspect the PNGs rather than treating a successful render as sufficient. Enable
the relevant persisted interaction checks for task drag/drop, drawers, timers,
calendar actions, or workspace creation. The available screenshot sections and
environment switches are documented in `README.md`.

## Build and verification

The SDK is pinned by `global.json`. Use the repository's restored package state and
disable the workload resolver in this environment:

```bash
dotnet build Snook.slnx --no-restore -p:MSBuildEnableWorkloadResolver=false -m:1 -nr:false -v:minimal
dotnet test Snook.slnx --no-build -p:MSBuildEnableWorkloadResolver=false -m:1 -nr:false -v:minimal
git diff --check
```

Run the smallest relevant test project while iterating, then run the full commands
above before handoff. If package inputs intentionally change, run restore explicitly
before returning to the pinned `--no-restore` workflow.

For a disposable embedded UI review:

```bash
SNOOK_DATA_DIR=/tmp/snook-ui-review dotnet run --project src/Snook.Desktop/Snook.Desktop.csproj --no-build
```

For headless UI capture after restore:

```bash
SNOOK_DATA_DIR=/tmp/snook-ui-data \
SNOOK_SCREENSHOT_DIR=/tmp/snook-ui-images \
./scripts/capture-ui-screenshots.sh
```

Use a fresh disposable `SNOOK_DATA_DIR`; never point tests or screenshot tooling at a
real workspace. Embedded mode is the default. Daemon-mode review requires a running
`snookd`, its token, and `SNOOK_HOST_MODE=daemon`. `scripts/package-rpm.sh` builds the
Fedora self-contained desktop RPM; packaging changes also require inspecting the
installed paths and launcher metadata.

## Documentation and scope claims

- Update `README.md` for user-visible setup, runtime, screenshot, or packaging changes.
- Update `docs/operations.md` for ownership, daemon hosting, backup, restore,
  migration, security, or recovery procedures.
- Update `docs/ui-design.md` when interaction or visual direction materially changes.
- Update `docs/progress.md` when implementation state, verification evidence, known
  limitations, or release scope materially changes.

Do not claim Android packaging/mobile UI, peer or cloud synchronization, Windows
service packaging, native human interaction review, assistive-technology validation,
or the release-readiness matrix as complete unless that work is actually implemented
and verified.
