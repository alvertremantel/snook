# Production-readiness status

Updated: 2026-09-18

This is the current release handoff for Snook. It summarizes the accumulated
daemon, persistence, client, UI, maintenance and packaging work and separates
implemented behavior from the remaining production gates. Detailed historical
evidence is in [progress.md](progress.md), and the daemon pass-by-pass record is
in [daemon-plan.md](daemon-plan.md).

Snook is a functional local desktop application with a shared embedded/daemon
backend contract. It is not yet production-ready or a supported multi-platform
release. Embedded and daemon modes operate on the same single-owner SQLite
workspace; daemon mode is authenticated loopback transport, not synchronization
or a second data store.

## Implemented so far

### Shared backend and durable mutation semantics

- Contract 1.6 exposes the current task, organization, timer, calendar, habit,
  journal, settings, batch and maintenance surface through both embedded and
  daemon clients. The CLI catalog is derived from that interface.
- SQLite schema 10 stores request-bound exact-result receipts. Stable operation
  retries return the original result after later edits, deletion, restart and
  backup/restore; changed arguments fail closed. Receipts, entity changes and
  mutation-log rows commit together.
- Change notifications are derived from committed mutation rows, delivered in
  commit order outside the write gate, and omit exact replay and rollback.
  Throwing and reentrant observers are isolated.
- Workspace ownership prevents two SQLite owners, recovers stale ownership and
  releases the lease on graceful daemon shutdown.

### Daemon transport and clients

- The daemon is loopback-only and authenticates health, RPC and SSE. Requests,
  bodies, concurrency, stream queues and write time are bounded. Dispatch is
  derived from the shared interface; malformed input is classified without
  exposing internal exception details.
- Token and endpoint-discovery files are private. Clients validate HTTP loopback
  endpoints, bypass proxies, refuse redirects, negotiate contract compatibility
  and fail closed on missing credentials, connectivity or protocol errors.
- Request handlers and streams are tracked during shutdown. Change streams use a
  single writer, bounded queue, heartbeat and reconnect invalidation behavior.
- GUI and CLI share device-local versioned connection profiles. Flags override
  environment, which overrides saved settings, with embedded as the no-profile
  default. Profiles never contain the bearer token. Unsafe, linked, oversized,
  nonprivate or malformed profiles fail before any workspace is opened.
- The GUI has a startup connection window, retryable validation/authentication
  errors, saved next-launch settings, connection settings from the main window,
  an explicit outage warning and manual refresh. It does not silently fall back
  to embedded storage or queue writes offline. The CLI can use the same saved
  profile without connection flags, explicitly bypass it with `--no-profile`,
  and reports the actual resolved daemon endpoint from `doctor`.

### Backup, restore and export

- Backup and JSON/CSV export require new absolute destinations, protect workspace
  and credential paths (including aliases and links), stage in private files,
  hash/flush before publication and never replace existing output.
- JSON export schema 5 contains all current feature families, persisted settings,
  a snapshot cursor, task relationships, tombstones and correction history. The
  format and exclusions are documented in [json-export.md](json-export.md).
- Restore requires a bounded format-1 manifest, verifies the staged byte count and
  SHA-256 before SQLite opens the candidate, validates and upgrades the staged
  database, excludes live reads/writes during activation and preserves the source.
- Running/open timers in a restored candidate are frozen at the manifest backup
  boundary and become explicit recovery items. Last known, Stop now and Continue
  have distinct elapsed-time policies and persist correction provenance.

### UI reliability and packaging

- Create/add flows retain caller-owned operation IDs and immutable arguments for
  safe retry after uncertain responses. Drafts survive refresh and revision
  conflicts; late responses do not overwrite newer drafts.
- Headless interaction checks cover the main workspace, timer recovery, connection
  configuration and daemon-backed task/timer flows at the minimum supported size.
- Fedora `linux-x64` packaging produces separate `snook`, `snook-cli` and `snookd`
  payloads, a desktop launcher with daemon/connection actions, documentation and an
  opt-in per-user systemd unit. Installation does not enable or start the service.
- Extracted-package and transient-user-service verification has covered discovery,
  authentication, CLI reads/writes/watch, shutdown, lease reopening and persisted
  restart behavior. No normal package or service was installed or enabled.

## Current verification baseline

The current source baseline builds with zero warnings/errors and has 149 passing
tests: 131 application tests and 18 domain tests. The application suite includes
real disposable daemon processes and loopback sockets. Seeded and empty 980×640
captures have been inspected, and daemon-backed connection, task and timer
interactions plus an actual daemon stop/restart with outage warning, reconnect and
draft retention have passed. Release-3 RPM evidence predates the later verified
restore, restored-timer and saved-connection-profile work, so it is not a release
artifact for the current tree.

## Remaining production gates

### P0: data safety and recovery

- Add a crash-consistent restore activation journal and deterministic startup
  recovery/rollback. Flush directory entries where supported and test process
  death, disk-full and I/O-failure points around candidate/archive activation.
- Add the specified direct timer-boundary editing recovery choice. Audit older
  malformed/incomplete timer rows, zero-duration history reachability, clock
  rollback and task parent/completion/dependency validity during recovery.
- Expand backup/restore operations with preview/verify/status semantics and bounded
  long-running jobs. Define cleanup for abandoned staging and partial publication.
- Complete Windows ACL/private-file behavior and native filesystem failure tests.

### P0: transport correctness under disruption

- Prove SSE snapshot/cursor ordering through reconnect and database restore,
  including gaps, queue overflow, slow subscribers, idle cleanup and daemon restart.
- Exercise request saturation, slow/partial bodies, cancellation at dispatch and
  shutdown boundaries, header limits, token rotation and protocol-version failures.
- Finish the requirement-by-requirement parity/bounds audit for every public method,
  particularly maintenance and older feature families.

### P1: client and UI recovery

- Complete retry retention for update, lifecycle and timer mutations, not only the
  create/add paths. Reconcile uncertain creates whose parent disappears and exact
  replays whose entity has since been deleted.
- Support deliberate live endpoint/token changes without losing an editor draft,
  and guard against connecting a visible window to a different workspace identity.
- Audit window/application shutdown while mutations or connection attempts are in
  flight. Extend outage checks to token rotation and deliberate endpoint changes.
- Perform native human keyboard, focus, screen-reader and high-contrast review;
  headless automation is useful evidence but not assistive-technology validation.

### P1: release and platform acceptance

- Rebuild and version the Fedora RPM from the current tree, then perform native
  install, upgrade and uninstall checks in a disposable Fedora environment. Inspect
  installed paths, launcher actions, user-unit behavior and ownership migration.
- Decide and implement the supported Windows background-service model, credential
  ACLs, install/upgrade flow and native acceptance matrix.
- Run the final threat, privacy, dependency/license and operational review; define
  supported backup recovery objectives, log retention and incident procedures.
- Re-run the full release matrix from clean sources and publish reproducible hashes.

Android/mobile packaging, peer/cloud synchronization and remote-network daemon
transport are separate product scopes and are not implied by completing these
local desktop production gates.

## Recommended completion order

1. Crash-safe restore activation and fault injection.
2. Stream/reconnect ordering and adversarial transport limits.
3. Remaining mutation retry and live-connection UI recovery.
4. Current-tree Fedora package lifecycle acceptance.
5. Windows packaging/service/ACL implementation and platform verification.
6. Final full-spec audit, accessibility/security review and release candidate gate.
