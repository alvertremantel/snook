# Snook desktop and daemon operations

## Embedded desktop

The default desktop profile owns one SQLite workspace at:

`<platform local application data>/Snook/workspace.db`

Set `SNOOK_DATA_DIR` to relocate the profile before starting Snook. Do not copy
an open database. Create a verified backup first, stop the owning host, then
move the complete `Snook` directory and start with the new data-root setting.

The `.owner` lease beside the database is the ownership guard. A second
embedded host or daemon using the same workspace must stop with a store-lock
error before it can mutate data.

## Daemon profile

Start the daemon interactively for diagnostics:

```bash
SNOOK_DATA_DIR=/var/lib/snook snookd
```

The daemon binds only to loopback, writes its high-entropy token to
`Snook/daemon.token`, and prints a JSON readiness line. Clients must use the
token and must not open SQLite directly when `SNOOK_HOST_MODE=daemon` is set.
`Ctrl-C` requests graceful shutdown; the listener closes and the SQLite lease is
released.

The authenticated readiness endpoint is `/v1/health`. The change stream is
`/v1/changes`; clients reconnect and refresh their bootstrap snapshot before
resuming push notifications.

## Backup and restore

Use Settings → Your data → Create backup, or the CLI/backend backup operation while the
owner is running. Backups use SQLite's online backup API and create a matching
`.manifest.json` containing byte count, SHA-256, and verification status.

Before restore, stop other clients that use the workspace. Restore replaces the
workspace only after the source is copied and `PRAGMA integrity_check` passes;
an interruption leaves the prior active database in place. Keep the manifest
with the backup when moving it between machines.

## Migration and recovery failures

- A schema checksum or migration failure is reported as an incompatible-store
  error; Snook does not reset the database.
- Preserve the original database and its logs, then retry from a verified
  backup or a copy of the workspace.
- A crash/clock anomaly marks an active session `recovery-required`. Resolve it
  in Today using **Last known**, **Stop now**, or **Continue**; the decision and
  provenance remain durable.
- If the owner lease remains after an unclean process exit, confirm no Snook
  host is running before removing only the stale `.owner` file.

## Linux service

`deploy/systemd/snookd.service` is a hardened template for a packaged
`/usr/bin/snookd`. Install it with the package's service-management procedure,
then inspect `journalctl -u snookd` for the JSON readiness line. SIGTERM is
handled as a graceful daemon shutdown, so the listener closes and the workspace
lease is released before systemd's stop timeout. Keep the data directory private
to the service identity and back up before upgrades or migrations.
