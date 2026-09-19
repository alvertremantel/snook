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

### Saved client connections

The GUI and CLI share a device-local version-1 `Snook/client-profile.json`, read
under the launch data root (`--data-dir`, then `SNOOK_DATA_DIR`, then platform
local-application-data). The saved data directory may point elsewhere; selecting
`--data-dir` again is an explicit override of that saved value. Precedence for
each field is flags → environment → saved profile → defaults. Daemon hosting does
not read this client file, so saving a profile never starts, stops or relocates
the service. Keep the daemon and effective client data roots aligned when using
automatic endpoint/token discovery.

Run `snook --configure` (or the desktop menu's Connection settings action) to
choose a connection without opening any workspace. At startup, **Remember these
settings** saves before attempting the connection, allowing an unavailable daemon
to remain the saved choice. **Connection…** in an open workspace saves only for
the next launch; the active backend and drafts are retained. Selecting embedded
is explicit and warns that other owners must be stopped. The default launcher
honors a saved daemon profile; without one or overrides it retains embedded mode.

Profiles contain `version`, `host` (`embedded` or `daemon`), `dataDirectory`,
optional `endpoint` and `tokenFile`. Tokens are never serialized. Reads are bounded
to 16 KiB and reject duplicate/unknown fields, unsupported versions, unsafe URLs,
links and nonprivate Unix files. Saves use a private staged file, flush, atomic
rename and a cross-process save lock with a content stamp: a stale dialog cannot
overwrite another window's settings. The `.lock` file is intentionally retained.
Workspace-local profile names are reserved against maintenance output. Profiles
are outside SQLite backup/export and must be configured on a new device.

A malformed/unreadable profile fails closed. The GUI shows a daemon-only recovery
draft, not an implicit embedded workspace. It can connect with entered settings
without saving over the invalid file. To repair persistence, preserve/move the bad
file aside and reopen configuration. CLI `--no-profile` requires an explicit host
selection (flag or environment); GUI `--no-profile` without a host opens selection
instead of automatically opening SQLite. Normal missing-token/authentication/
connection errors keep the GUI connection form visible for correction and Retry.
Cancel/Escape cancels and drains an in-flight attempt; a late backend is disposed
instead of opening a window after cancellation.

An established daemon connection shows its actual endpoint in the workspace
footer. A detected outage warns that the view is stale and offers **Retry refresh**;
drafts remain visible and writes are not queued offline. Automatic stream reconnect
refreshes the same endpoint/token. Changing ports or rotating credentials currently
requires reopening the app to reread discovery/token files—saving new settings
does not live-switch an open workspace. Reconcile uncertain writes before closing
drafts; broader lifecycle retry and reconnect reconciliation remain acceptance work.
These profiles do not implement remote/TLS hosting, sync or offline replication.

### Starting the host

Start the daemon interactively for diagnostics:

```bash
snookd --data-dir /absolute/private/data-root
```

The daemon binds only to loopback, writes its high-entropy token to
`Snook/daemon.token`, and prints a JSON readiness line. Clients must use the
token and must not open SQLite directly when `SNOOK_HOST_MODE=daemon` is set.
`Ctrl-C` requests graceful shutdown; the listener closes and the SQLite lease is
released.

The authenticated readiness endpoint is `/v1/health`. The change stream is
`/v1/changes`; clients reconnect and refresh their bootstrap snapshot before
resuming push notifications.

GUI and CLI can connect simultaneously. Use `--host daemon --data-dir PATH`
on either executable (or `SNOOK_HOST_MODE=daemon` and `SNOOK_DATA_DIR`). An
alternate endpoint uses `--endpoint http://127.0.0.1:PORT/`; an alternate private
token file uses `--token-file PATH` or `SNOOK_DAEMON_TOKEN_FILE`. Explicit options
override environment defaults. The daemon reads `SNOOK_DAEMON_PORT`; invalid
values fail startup instead of silently selecting a different port. Clients
never probe alternative ports or switch to embedded mode. An invalid desktop
host-mode value also fails before opening a database.

The client authenticates health and checks contract major equality and a server
minor version at least as new as its own before issuing RPCs. Upgrade daemon and
clients together. Only HTTP loopback root URLs are supported; redirects and
proxies are disabled. Remote access requires a separately designed transport.

After binding, the daemon atomically publishes `Snook/daemon.endpoint.json`
with its endpoint, instance ID and contract version, and removes it at graceful
shutdown. It contains no credential. Clients with no explicit endpoint/port
read this private bounded descriptor, making `--host daemon --data-dir PATH`
sufficient even on a custom port. An invalid descriptor fails closed. A stale
descriptor after a crash is replaced when the daemon restarts; it never grants
database ownership to clients. Daemon `--data-dir` and `--port` override their
environment defaults; `--help` exits without opening a workspace.

The daemon admits up to 64 active requests, including up to 16 change streams.
RPC bodies are bounded to 1 MiB with at most 64 arguments and a 30-second
deadline including dispatch wait. RPCs execute serially through the shared
backend; no request task outlives backend shutdown. Each SSE connection has a
single writer, a 256-event queue, a ten-second heartbeat and a five-second write
deadline. Overflow or disconnection closes that stream; clients reconnect and
refresh a bootstrap snapshot. This is snapshot recovery, not durable SSE cursor
replay. Request overload returns HTTP 503. Malformed JSON and argument shapes
produce structured validation errors. Unexpected exception text is not exposed.

Entity notifications come directly from the persisted mutation-log rows of each
fresh transaction. Their cursor, operation/aggregate IDs, kind, revision and UTC
millisecond timestamp match that transaction; receipt replay and rollback produce
no event. This applies to embedded and daemon clients, including older task,
organization, timer and calendar operations. Observers run outside the writer
gate in commit order; an observer exception cannot make a committed write fail
or prevent other observers from receiving it. Event handlers must return promptly
and queue longer work. Workspace restore is a separate `workspace/restored`
snapshot invalidation with an empty operation ID because it replaces the mutation
history; clients must reload it even if the restored cursor is lower.

On Unix the daemon profile directory is owner-only and newly created token
files are mode 0600 from creation. Existing tokens must be private, bounded and
contain a 64-character hexadecimal credential; symbolic-link token files and
profile directories are rejected. For rotation, stop the daemon, move its token
file to a private recovery location, and restart to generate a new credential.
Restart clients so they load the new file. Rotation does not change workspace
data. Preserve a private OS account/profile on Windows; Windows ACL and service
verification remain part of the platform acceptance work.

For a failed connection, check that the selected daemon is running, that the
client selected its data directory and port, and that its token file is readable.
`snook-cli ... doctor` provides structured diagnostics. Once connected, the GUI and
CLI watch stream refresh after a daemon restart. Failed writes can have uncertain
outcomes: reuse the original operation ID and payload for methods supporting
receipts. Contract 1.6 gives all entity create/add operations a caller request;
legacy calls that omit it still require checking saved data before retry.
Do not automatically repeat an invocation that lacked an operation ID.

The GUI retains operation IDs and captured arguments for organization/task/calendar
creation, manual time, and tag/link/dependency additions while an unchanged
draft/quick action is pending. A user retry confirms or completes that same
operation, including after a lost response. Quick manual time keeps its original
activity and interval; planned tasks keep the captured activity default; local
date inputs keep the original instant/time-zone interpretation. Success clears
the attempt before refresh. A refresh failure after success is not an invitation
to repeat the creation. Changes typed while a save is pending remain visible,
and an older response cannot close a newly opened creation drawer.

Cancel/Escape discards the local attempt, not a mutation already sent to the
daemon. If its response arrives later, the GUI reports the saved creation without
overwriting the current draft. If an older discarded attempt instead remains
uncertain, it asks the user to inspect saved data, not retry the new draft.
Cancellation during preliminary task lookup
prevents dispatch. These attempts are in memory, not a durable offline outbox;
after closing/restarting the GUI, inspect current data before recreating an
uncertain item. Older update/lifecycle actions still need the separate UI retry
audit tracked in the daemon plan. No migration beyond schema 10 is required for
the new create-request support, but deploy matching 1.6 clients and daemon.

The headless CLI uses this same boundary. Configure it with `snook-cli --host daemon --endpoint http://127.0.0.1:43871/ --token-file <data-root>/Snook/daemon.token doctor`, or the corresponding `SNOOK_*` environment variables. It fails closed
in daemon mode: a missing token, failed authentication, unavailable endpoint, or
protocol failure does not permit it to open the workspace database. This keeps
the CLI configuration compatible with a future server/daemon transport without
silently changing ownership mode.

## Backup and restore

Use Settings → Your data → Create backup, or the CLI/backend backup operation while the
owner is running. Backups use SQLite's online backup API and create a matching
`.manifest.json` containing byte count, SHA-256, and verification status.
The supported backup/restore size is at most 16 GiB; source page counts are checked
before online backup starts. Manifests are bounded to 16 KiB.

Backup and JSON/CSV export destinations must be absolute, unused host-local file
paths. Existing files are never overwritten. Backup also refuses an existing
manifest, SQLite WAL/SHM/journal or owner companion. Workspace database/sidecar/
lease/recovery names and daemon credential/discovery names are reserved, including
case variants and resolved workspace directory aliases. Output paths reject
symbolic links/junctions, traversal components and special device/file names;
paths are limited to 4096 characters and 128 components. Pick a new file name for
each intentional run. The GUI's timestamped names can collide if invoked twice
in the same second; the second operation reports an error, preserving the first.

Artifacts are built in a private staging directory beside the destination.
Backup checks SQLite integrity and foreign keys before calculating the hash and
publishing its manifest. Exports hold the shared writer gate while collecting
data, so a concurrent mutation or restore cannot mix states between queries.
Completed files are flushed and moved without replacement; on Unix, files are
mode 0600 and staging directories mode 0700. Windows ACL validation is still a
separate platform gate. Use destination directories under your control: these
path checks are not a defense against a hostile process that can replace parent
directories between filesystem calls.

The backup database is published before its manifest; a filesystem failure or
process interruption between these two moves can leave a complete database
without the completion manifest. Do not treat a manifest-less result as a
confirmed backup. An interrupted operation can also leave a private
`.snook-artifact-*` staging directory. Preserve it for inspection, or remove only
that confirmed abandoned directory after the host has stopped. Never clean up
by deleting a caller-selected parent directory. Maintenance operations have no
entity receipt: after an uncertain response inspect existing artifacts and their
hashes instead of assuming a repeated call will replay. Cancellation before
publication leaves no final artifact; cancellation is not checked after the
publication commit point. Filesystem errors after the first backup move can
still leave the incomplete pair described above. Directory-entry durability
across sudden power loss is not established by the file flush alone.

Before restore, stop other clients that use the workspace, leaving the selected
owning daemon running to perform it. Restore now requires a standalone database
and matching format-1 `<backup>.manifest.json`; raw database copies and incomplete
backup pairs are not normal restore inputs. Required manifest fields are
`formatVersion: 1`, `createdAtUtc` (UTC), `databasePath` (informational original
location), positive `bytes`, a 64-digit hexadecimal `sha256`, and
`integrity: "sqlite-backup-verified"`. Duplicate keys, malformed JSON, missing
required fields, unsupported values, oversize files and hash/length mismatches
fail before the active workspace changes. Additional nonconflicting metadata is ignored. A hash
is an integrity check, not a signature: only restore backups from a trusted source.

Source/manifest files and source ancestors cannot be symbolic links or junctions;
live workspace/credential paths and sources with WAL, SHM, journal or owner
companions are rejected. Create a new online backup through the known healthy
owning host rather than copying an active database or deleting its sidecars.
Keep manifests when moving backups. A valid pair remains restorable after moving
between paths/machines; restore never follows the original path in the manifest.
Do not manufacture a new manifest for an unknown or incomplete file. Recover its
original manifest or take a new verified backup. Valid historical format-1 pairs
remain supported; test fixtures for older schemas include their own matching
manifests rather than bypassing verification.

Restore copies the bounded source into a private `.snook-restore-*` directory
beside the live store, then hashes those staged bytes before opening them with
SQLite. It never opens the original source with SQLite or creates source sidecars.
Staging uses `trusted_schema=OFF`, `quick_check`, foreign-key checks and checked
schema upgrades; the candidate is checkpointed, hashed and flushed before
activation. Native workspace connections are leased through a shared gate:
restore waits for existing readers to close and excludes new reads/writes during
the operation. Store disposal drains these connections before releasing ownership.
Maintenance therefore temporarily pauses reads as well as writes.

Restoring a backup does not resume its running timers. In the staging transaction,
each nondeleted running session (or session with an open interval) becomes
`RecoveryRequired` with `recoveryStatus: "workspace-restored"`. Its open interval
ends at the manifest's UTC backup boundary, or its start if the backup clock was
earlier. Existing closed intervals and their IDs remain intact. Closed paused and
stopped sessions are unchanged. Re-backing up/restoring a closed unresolved item
does not advance that boundary. New backups capture `createdAtUtc` immediately
before the SQLite snapshot, while the writer gate is held, not after hashing it.

Dashboard recovery cards offer three explicit choices:

- **Last known** stops at the latest recorded boundary, adding no time.
- **Stop now** stops now and explicitly adds the gap as a closed
  `recovery-stop-now` interval.
- **Continue** starts a new `recovery-continue` interval now, without crediting
  the gap. It validates live task/activity references and honors the configured
  foreground concurrency policy.

If the current clock precedes the frozen boundary, Stop now and Continue fail
without changing the item; fix the clock or choose Last known. Preparation and
the explicit decision each retain before/after correction provenance. Resolution
uses the normal revision check and exact receipt replay; retry the same request
after an uncertain response. Restoring preserves old receipts, so replaying an
old start can return its original running result without changing the actual
recovery item. Refresh the snapshot to read current state. The prepared candidate's
cursor and hash include these recovery changes and may differ from the source
backup; source backup bytes/manifest are never rewritten. History and summary
count only frozen recorded intervals until a decision explicitly adds time.

Cancellation before activation leaves the prior workspace intact. Once activation
succeeds there is no further cancellable read/hash that can turn success into a
reported cancellation. A single `workspace/restored` invalidation is queued with
the candidate's captured cursor/identity before releasing the writer gate; it
uses the ordered observer-safe delivery path rather than reloading potentially
newer state afterward. It is not a mutation-log row or a replay receipt. Even if
an observer throws, starts a new mutation or cancels the caller, the committed
restore retains its result and notification order.

The prior database and any WAL/SHM files are retained as uniquely named
`workspace.db.before-restore-*` recovery files. Filesystem failures during the
switch attempt rollback, but the pair of renames is not a power-loss-atomic
transaction. If the active database is missing and restore archives/staging
exist, startup fails closed instead of initializing an empty workspace. Keep the
host stopped, preserve the complete profile and recovery files, and use an
explicit recovery procedure; do not delete the archives to bypass this check.
Automatic crash-recovery selection and directory-entry durability still require
further implementation/fault-injection acceptance. These archives are not
manifest-backed backup pairs and are not accepted directly by normal restore.

## Migration and recovery failures

Schema migration 10 (`exact-operation-receipts`) adds a separate receipt table
for older organization, task, timer, calendar, settings and batch commands. New
receipts store the original serialized result and a hash of the command identity
and arguments, including expected revisions. They are committed with the mutation
and survive subsequent edits, soft deletion, restart, backup and restore. Reusing
an operation ID with changed arguments is rejected without applying a new write.
No-op commands also reserve their IDs and retain their original results. Host
clock values and current timer-concurrency policy are not part of caller input.

Pre-migration receipts are preserved. Older receipts without a verifiable request
and original result cannot be reconstructed safely: their retries fail explicitly
and require reviewing current data before starting a new operation. Existing habit
and journal receipts keep their established exact-replay formats. Keep client and
daemon versions aligned; older hosts reject schema 10 rather than reinterpret it.
Initialization and staged restore verify each applied migration checksum from
sequence 7 onward, so a later migration cannot conceal an earlier checksum failure.

Online database backups include the new receipt table. Restore upgrades older
supported databases in staging without modifying the source. JSON export schema 5
and CSV still export their documented domain data; they do not export the internal
retry ledger and are not substitutes for database backups when retaining retry
history is required. Restore itself remains a workspace replacement operation,
not an idempotent entity command with a caller-supplied operation ID.

JSON schema 5 adds persisted settings, the snapshot cursor, task links and
dependency edges, and preserves stored tag/session/link deletion history. The
same exporter runs in embedded and daemon modes. See [the format reference](json-export.md)
for exact sections, field casing, enums, exclusions and compatibility. It is not
a JSON import/restore format; `RestoreBackupAsync` accepts SQLite backups.

Schema migration 9 (`journals`) adds `journals`, `journal_entries`, and
`journal_entry_tags` transactionally under the existing ownership lease. Journal
tags have no relationship to task/project/activity tags. Contract 1.5 exposes
the same journal reads and writes in embedded, authenticated daemon, and CLI
clients; the daemon's interface-derived allowlist includes all journal methods.
Upgrade desktop, CLI, and daemon together.

Journal and entry mutations require operation/device IDs, plus the current
aggregate revision for updates or delete/restore. The receipt, content, tags,
revision, and committed change are saved in one transaction. Exact retries return
the original result even after later edits, deletion, or restart, without a second
notification. Deleting a journal hides its entries without changing their individual
deleted states. Restoring it reveals entries that were not separately deleted.
Entries retain UTC occurrence, creation, and update instants; desktop display uses
local time. Mood is nullable or an integer from 1 through 7.

JSON export schema 4 includes every journal and entry, including deleted records,
under `journaling`; each entry includes its journal-only tags. Existing export
sections remain available. CSV remains a time-session report. Database backups
include all journal tables and receipts. Restore upgrades older supported backups
in the staging copy before replacement and never changes the source backup.
Journal queries page 1–100 entries in occurrence-time/ID order. Continuation tokens
must be reused with the same filters. Catalog limits are 200 journals including
deleted journals and 1000 distinct tags per tag-list query; larger tag catalogs
remain readable through paginated entries.

Schema migration 8 (`daily-habits`) adds habits and their per-day check-ins in a
transaction under the existing ownership lease, with a separate checksum. It
does not alter tasks, calendar recurrence, or tracking sessions. Contract 1.4
adds habit reads and revision-checked writes to both hosts; the daemon dispatcher
uses the interface allowlist. Upgrade desktop, CLI, and daemon together.

Habit writes persist an exact, request-bound result receipt alongside the
aggregate revision and committed change. Exact retries, including after restart
or later edits, return that result without repeating the mutation or notification.
Archive/delete retain history; restore does not reinterpret dates. Habits store
their creation time zone, check-ins store civil dates plus UTC recording instants,
and corrections are limited to the last 366 days. The initial catalog limit is
500 habits including deleted records. JSON export schema 3 includes all habits
and check-ins under `habitTracking`; CSV remains a time-session report. Database
backups contain the habit tables and receipts. Restore upgrades a v7 backup in
its staging copy before replacing the workspace, so habit reads work immediately;
the original backup remains untouched.

Schema migration 7 (`task-due-dates-and-favorites`) indexes open date-only deadlines
and starred tasks/projects. Existing schemas already contain `tasks.due_date`
and favorite columns, so the migration preserves their values and all calendar
blocks instead of adding a duplicate due-date column or deriving deadlines from
schedule times. It runs transactionally under the ownership lease and has its own
checksum. As with other upgrades, keep a verified backup before upgrading.

Contract 1.3 adds `BulkUpdateTasksAsync` to both embedded and daemon clients.
Batches accept 1–500 distinct task IDs and expected revisions, use one SQLite
transaction, and create one `task-batch` committed change. The operation receipt
stores the affected tasks and their exact committed revisions for replay, even
after later edits or restart. A failed batch rolls back task fields, tag changes,
the receipt, and the change log. UI retries reuse the operation ID for an unchanged
request; explicitly loading latest task revisions creates a new attempt.

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

The Fedora RPM installs `deploy/systemd/snookd.service` into
`/usr/lib/systemd/user`, plus `snook` (GUI), `snook-cli` and `snookd` launchers.
Installation does not start/enable the service or open a workspace. The user
service runs as the signed-in user, so the GUI and CLI can read its private token.
It uses the same platform local-application-data default as both clients. It is
not a system-wide `DynamicUser` service; never loosen token permissions to share
it with another account.

If you previously installed the old system-wide template manually, stop and
disable that system unit before starting the user unit. Back up its workspace
under its existing identity first. This package does not migrate ownership or
move `/var/lib/snook` data; plan an explicit backup/restore into the user's
private profile rather than making the old token or database world-readable.

Close any embedded GUI and finish embedded CLI work before enabling the service:

```bash
systemctl --user daemon-reload
systemctl --user enable --now snookd.service
systemctl --user status snookd.service
journalctl --user -u snookd.service -n 30 --no-pager
snook-cli --host daemon doctor
snook --host daemon
```

`Type=exec` confirms process execution, not application readiness. The JSON
readiness log and a successful authenticated `doctor` confirm readiness. Startup
may fail on a live embedded owner, incompatible database, or occupied port;
inspect diagnostics instead of deleting the lock or resetting data. Restarts are
limited to three starts per minute to avoid a persistent failure loop. After
fixing a startup failure, use `systemctl --user reset-failed snookd.service`, then
`systemctl --user start snookd.service`.

For a custom data directory or port, use `systemctl --user edit snookd.service`:

```ini
[Service]
Environment="SNOOK_DATA_DIR=/absolute/private/data-root"
Environment="SNOOK_DAEMON_PORT=43872"
```

Restart the service and pass the same `--data-dir` to both clients. The endpoint
descriptor discovers the selected port. Shell environment changes do not
automatically change an already-running user manager or service. Each user on a
multi-user machine needs a distinct loopback port. Do not put tokens in unit files,
drop-ins, command arguments or logs. Per-user unit hardening uses `UMask=0077` and
`NoNewPrivileges=yes`; it intentionally avoids namespace-dependent filesystem
restrictions that may be unsupported in user managers or block user-selected
backup/export destinations. See the [systemd execution reference](https://www.freedesktop.org/software/systemd/man/latest/systemd.exec.html).

Without lingering, the user manager's lifetime follows the system's session
policy. Continuing across logout requires an explicit administrator/user decision
about `loginctl enable-linger`; Snook never enables it during installation.

Before upgrading, create and verify a backup, close clients, and stop the user
service with `systemctl --user stop snookd.service`. Upgrade the package, run
`systemctl --user daemon-reload`, start the service, and check `doctor` before
reopening clients. An already-running service is not automatically restarted by
the RPM. Older binaries may reject a migrated workspace; rollback requires the
matching backup and compatible binaries, not an in-place database downgrade.

To return to embedded mode, close daemon clients and run
`systemctl --user disable --now snookd.service` before opening the normal GUI.
Stop/disable the service before uninstalling as well. Uninstallation does not
delete personal workspace data. SIGTERM drains the daemon and releases the lease;
the unit permits 45 seconds before forced termination. A forced stop retains
SQLite recovery semantics and requires inspection on next startup.
