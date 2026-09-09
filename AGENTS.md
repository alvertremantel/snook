# Snook contributor notes

## Project shape

Snook is a .NET 10 Avalonia desktop application. The normal desktop path is embedded SQLite; daemon mode is an explicitly selected, authenticated loopback HTTP path. Keep the backend contract in `src/Snook.Contracts` shared by both hosts.

The main layers are:

- `src/Snook.Domain`: immutable domain records, validation, time and recurrence rules.
- `src/Snook.Contracts`: `IBackendClient`, DTOs, operation requests, and change notifications.
- `src/Snook.Persistence.Sqlite`: SQLite schema, migrations, transactional persistence, backups, and exports.
- `src/Snook.Application`: backend orchestration and business workflows.
- `src/Snook.UI`: Avalonia views and view models shared by desktop hosts.
- `src/Snook.Desktop`: embedded/daemon host selection and Avalonia startup.
- `src/Snook.Daemon`: authenticated loopback RPC and SSE change stream.
- `src/Snook.Cli`: command-line host for bootstrap and summary workflows.
- `tests`: domain and application contract tests.

## Working rules

- Preserve user changes in a dirty worktree. Inspect `git status` before editing.
- Use `apply_patch` for hand edits.
- Keep mutations transactional and revision-checked. New mutable aggregates should have stable IDs, revisions, operation IDs, and change notifications where appropriate.
- Keep embedded and daemon clients behaviorally equivalent. Add or update contract tests when adding an `IBackendClient` method.
- Do not add silent SQLite fallback when `SNOOK_HOST_MODE=daemon`; daemon mode must fail clearly if authentication or the daemon is unavailable.
- Treat soft delete, restore, idempotency, migrations, and backup/restore behavior as first-class requirements.
- Avoid unbounded recurrence expansion and validate external paths/URIs at the boundary.
- Keep secrets in the daemon token file with restrictive permissions; do not log tokens.

## Verification

Use the repository's pinned restore state and disable the workload resolver in this environment:

```bash
dotnet build Snook.slnx --no-restore -p:MSBuildEnableWorkloadResolver=false -m:1 -nr:false -v:minimal
dotnet test Snook.slnx --no-build -p:MSBuildEnableWorkloadResolver=false -m:1 -nr:false -v:minimal
git diff --check
```

The desktop app can be launched from the built output with `SNOOK_DATA_DIR` pointed at a disposable directory for UI review. Embedded mode is the default. Daemon mode requires `SNOOK_HOST_MODE=daemon` and a running `snookd` with its token.

## Documentation and handoff

Update `README.md` for user-facing setup changes, `docs/operations.md` for backup/recovery/hosting procedures, and `docs/progress.md` when the implementation state or release scope materially changes. Do not describe Android packaging, peer sync, Windows service packaging, or release-gate/accessibility validation as complete unless they are actually implemented and verified.
