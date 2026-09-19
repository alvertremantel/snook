# Daemon implementation and verification plan

The target is the complete current `IBackendClient` surface in a reliable,
authenticated daemon, including tasks/batches, timers/history, calendar/recurrence,
habits, journals, settings, backup/export and staged restore. The original
specification is under `.opencode/artifacts/specs/spec-2026-09-08-snook`.
The repository agent guide specifies authenticated loopback RPC/SSE as the current
transport. This supersedes the older gRPC/named-pipe/UDS transport proposal for
this implementation; it does not relax ownership, privacy or parity requirements.
Remote access and synchronization remain separate features.

## Work and acceptance evidence

1. Harden request processing: interface-derived dispatch, typed malformed-input
   errors, sanitized internal errors, bounded bodies/concurrency/deadlines, tracked
   shutdown, and no client credentials sent to remote hosts or redirects.
   Verify with adversarial HTTP tests and process lifecycle tests.
2. Make changes reliable: one writer and bounded queue per stream, heartbeats,
   slow/disconnected subscriber cleanup, reconnect snapshot recovery, version
   negotiation, cancellation and asynchronous disposal. Verify concurrent writers,
   reconnect after restart, authentication/protocol failures, and no replay event.
3. Protect credentials and discovery: private creation, validation, rotation,
   startup failure hygiene, protected endpoint descriptor and explicit configuration
   failures. Verify permissions, competing owners, invalid configuration, and no
   token/content in diagnostics.
4. Run identical semantic workflows through embedded and daemon adapters across
   every current feature family. Audit create-operation retry gaps, actual commit
   cursors/timestamps, path safety, bounded reads and recovery semantics; close
   gaps in the shared backend rather than adding daemon-only business rules.
5. Deliver per-user Linux service operation, installation/upgrade/stop/recovery
   guidance, client outage behavior and a complete wire reference. Assess Windows
   service requirements separately and state unverified platform gates explicitly.
6. Finish with full solution build, full tests and `git diff --check`; audit the
   above against current code and executable evidence. Platform/service and UI
   checks must be reported at their actual verification scope.

The user's GUI/CLI usability requirement includes simultaneous clients, matching
profile/credential resolution, discoverable launch options, a persistent profile
workflow, and clear outage/retry behavior. Audit packaged executable names too:
the existing RPM's `snook` launcher opens the GUI, whereas CLI documentation uses
`snook` for the headless binary. Resolve packaging/discovery without silently
changing an installed user's launcher behavior.

## Initial audit (2026-09-18)

The current interface already routes the latest batch/habit/journal features.
Existing tests exercise those shared scenarios through the daemon. Gaps include
untracked request handlers at shutdown, simultaneous SSE stream writes, unbounded
subscribers without heartbeat cleanup, raw exception messages, malformed JSON
classified as internal errors, no version check, token permissions applied after
writing, permissive client endpoints/redirects, and a system-account service
template despite the specification's per-user default. These remain acceptance
work until implementation and focused checks demonstrate otherwise.

## Transport and client pass

Implemented request tracking/drain, serial contract dispatch, null/type/envelope
validation, private token creation and descriptor discovery, bounded SSE queues
with one writer, heartbeat/write deadlines, authenticated compatibility checks,
loopback-only client endpoints with redirects/proxies disabled, asynchronous
client disposal, and shared desktop/CLI connection resolution. Desktop and daemon
now expose launch options and `--help`; clients discover custom daemon ports from
their selected data directory. README, CLI and operations instructions describe
the simultaneous GUI/CLI workflow and uncertain-write retries.

The focused transport suite covers malformed calls, non-contract dispatch,
credential/descriptor permissions, unsafe endpoints, incompatible health,
missing-token fail-closed CLI behavior, concurrent event ordering, custom-port
discovery, CLI doctor with a live daemon, SIGTERM, descriptor removal and lease
reacquisition. This does not prove the whole daemon acceptance scope.

Verification for this pass: full solution build passed with zero warnings/errors;
all 83 tests passed (65 application, 18 domain); `git diff --check` passed.
Desktop and daemon `--help` both exited successfully without opening a workspace.
Tests ran against disposable profiles with local process/socket permissions.

Remaining substantive work includes:

- Finish the broader mutation-boundary audit, including caller-controlled bounds,
  maintenance path safety and remaining request validation. Exact results and
  command-argument binding for new entity receipts now use the shared transaction
  boundary; unverifiable legacy receipts fail explicitly.
- Complete the broader GUI update/lifecycle retry audit after the caller-owned
  create-operation pass below; verify real daemon-backed GUI outages as well as
  the simulated lost-response draft tests.
- Cover snapshot/stream ordering at reconnect and restore, cursor gap recovery,
  subscriber overflow/idle cleanup, request saturation/slow bodies, and client
  cancellation/host restart using executable adversarial tests. Audit pre-dispatch
  HTTP connection/header bounds as well as the implemented request-task limits.
- Finish native package install/upgrade/uninstall acceptance after the per-user
  Linux service/package integration pass below. Add a persistent GUI profile workflow
  and visible startup/outage recovery, then test actual daemon-backed GUI flows.
- Extend equivalent embedded/daemon scenarios to older feature families and
  maintenance paths; audit bounds, paths and mutation invariants. Windows service,
  ACL and native platform acceptance must remain explicit until verified.

## Transaction notification pass

`SqliteStore.WriteAsync` reads newly inserted mutation-log rows inside its
transaction, enqueues them only after commit, and delivers them outside the writer
gate in commit order. `SnookBackend` maps this metadata to the shared notification
contract. All entity mutation families now use that path; ad hoc post-write cursor
reloads and duplicate habit/journal publishers were removed. Receipt replay and
rollback produce no event. Observer exceptions are isolated, and a handler may
reenter the store without deadlocking the writer. Restore retains an explicit
snapshot invalidation because replacing the database also replaces cursor history.

New parity scenarios compare notifications with persisted SQLite rows through
both embedded and daemon adapters, including retries, stale revisions, timer
lifecycle, organization/tasks/tags, settings, calendar and bulk edits, and concurrent
writes. Separate cases exercise sub-millisecond clock inputs, restart replay,
receipt-insertion rollback, visibility after commit, and throwing/reentrant
observers. Exact historical result receipts and full restore/stream reconciliation
remain separate acceptance work.

Verification: a clean full solution rebuild passed with zero warnings/errors;
all 86 tests passed (68 application, 18 domain), including the new restart/rollback
case, and `git diff --check` passed. The clean rebuild replaced stale incremental
test output detected during test-discovery verification. No schema or public
method signatures changed in this pass.

## Exact receipt pass

Migration 10 adds `exact_operation_receipts`. All 43 older SQLite write paths and
the batch write path now pass an explicit command identity and caller arguments
through the transaction wrapper. The wrapper checks for an exact saved result
before executing the action, rejects changed command arguments, and saves the
result atomically with the mutation, base receipt and log entry. No-op results
also reserve their operation IDs. The old helpers that reconstructed results
from current entities were removed. Existing habit/journal receipt formats remain
supported; legacy receipts lacking a verifiable original result fail closed.

Default task activity resolution now occurs after receipt lookup inside the timer
transaction. Replays therefore do not reinterpret task defaults or concurrency
settings. Older operation-request boundaries now validate operation/device IDs,
positive expected revisions, and whether an aggregate revision applies. Startup
and staged restore apply migration 10 and validate every applied checksum from
sequence 7 onward. Historical SQL is unchanged. Backups include the receipt table;
JSON export stays at schema 4 and exports domain data rather than retry metadata.

Tests exercise exact replay after later edits, soft deletion, backup/restore,
restart and daemon-to-embedded reopening; changed payload/revision/command rejection;
no-op and create receipts; legacy v9 staged restore without source mutation;
earlier checksum corruption; and rollback if saving the exact receipt fails.
Caller-supplied operation IDs for remaining convenience creates, GUI retention of
uncertain create requests, and maintenance-operation semantics remain open work.

Verification: clean full solution rebuild with zero warnings/errors, all 90 tests
passing (72 application, 18 domain), and clean `git diff --check`. The shared daemon
scenario also reopens the same disposable store in embedded mode after SIGTERM
and verifies the saved results. Migration fixtures were updated to remove only the
new table/ledger entry when constructing older schemas; no historical SQL changed.

## Linux packaging and CLI operation pass

The RPM now publishes three separate self-contained payloads and preserves the
installed GUI command `snook`. The CLI is installed as `snook-cli` and the daemon
as `snookd`. CLI help/errors and the command reference use the installed name;
the raw CLI assembly name remains `snook`. CLI and operations documentation and
an opt-in per-user systemd unit are packaged. No install script enables/starts a
service or opens/migrates a workspace. The prior system-account template was
replaced with a user unit using the client's local-application-data default,
private umask, no-new-privileges, bounded restart attempts, and a graceful-stop
window. Documentation covers matching client profiles, custom ports, readiness,
logout lifetime, switching ownership, upgrades and stop-before-uninstall.

Packaging uses a fresh build subdirectory rather than recursively deleting a
caller-supplied work path. Version overrides now reach the RPM spec, and
`--install` selects only the package produced by that invocation. New verification
scripts inspect the actual built RPM and exercise extracted payloads; a separate
transient-service check imports the shipped unit's properties while substituting
only its executable path and disposable profile/port. Neither verifier installs
the package or enables the normal service.

The package smoke test exposed multi-line output from `watch` despite the NDJSON
documentation. Stream records now use compact JSON independently of pretty-printed
one-shot results. The daemon CLI integration test parses complete ready,
reconnected and committed-change records and rejects embedded physical newlines.
Snapshot/reconnect ordering remains the separate stream acceptance item above.

Verification: full restore/build with zero warnings/errors, all 90 tests
passing (72 application, 18 domain), and `git diff --check`. The transient user
service on Fedora 44 passed authenticated readiness, effective Type/UMask/
NoNewPrivileges checks, private token, mutation persistence across managed restart,
redacted journal, graceful stop, descriptor removal and embedded lease reopening.
The final rebuilt RPM smoke check is recorded below. Native
installation/upgrade/uninstall, GUI rendering and Windows service acceptance are
not established by these extracted-payload/transient-unit tests.

Final artifact: `artifacts/rpm/snook-0.1.0-2.fc44.x86_64.rpm`, SHA-256
`9b6358651b45faf591dde86033567ec61a32237b176fe890a06c513b28138d80`.
Release 2 allows upgrades from the earlier GUI-only release 1. The three payloads
were published by `package-rpm.sh`; the release-number/documentation-only update
was repackaged from the same published archive with the current spec. Native
dependencies include the dynamically loaded .NET facilities (ICU, OpenSSL,
Kerberos, certificates and time-zone data); private bundled runtime libraries no
longer advertise global RPM provides or a private debugger-library requirement.

`verify-rpm.sh` passed against that exact release-2 artifact on Fedora 44:
installed paths and wrapper targets, preserved desktop launcher, unit contents,
documentation byte equality, no install scriptlets, native dependency metadata,
GUI/CLI/daemon help, custom-port discovery, CLI doctor/read/write/live NDJSON watch,
ownership/missing-token fail-closed errors, SIGINT/SIGTERM, private token permissions,
descriptor removal and persisted data after embedded reopening. Extraction is at
`/tmp/snook-rpm-check.VNFBaTPg`. `verify-user-service.sh` then passed with those
same extracted payloads; disposable service evidence is at
`/tmp/snook-systemd-check.Qf7Sl5k6`. The temporary unit was stopped/collected; no
normal service or package was installed/enabled. Final full solution build and
all 90 tests passed again, and shell syntax / `git diff --check` passed.

Remaining GUI/CLI work includes a saved daemon connection profile, visible
startup/outage recovery, the broader update/lifecycle retry audit, actual
daemon-backed GUI interaction checks, and the separate
snapshot/reconnect ordering audit. Do not interpret this packaging pass as full
daemon production acceptance or as native package lifecycle verification.

## Caller-owned create and GUI retry pass

Contract 1.6 adds optional caller operation requests to the 13 remaining
organization/task/calendar create and tag/link/dependency add methods. Embedded
and daemon adapters use the same validated IDs and exact-result receipts. Old
wire calls may omit the new optional argument, preserving their former behavior;
such calls are not safe to repeat automatically after an uncertain result.
Current clients require a 1.6 daemon. In-process C# callers must rebuild and use
named cancellation tokens where they previously passed one positionally.

The GUI now retains a request and immutable arguments per unchanged creation
draft or quick action, including manual time and row-owned tag/link/dependency
additions. Derived times, local-zone interpretation and task activity defaults
are captured once rather than recomputed on retry. Success clears the attempt
before refreshing. Late results cannot erase newly typed fields or close a
reopened drawer. Cancel during preliminary lookup prevents dispatch; Cancel
after sending discards only the local attempt and does not undo a commit.
Discarded uncertain attempts ask the user to inspect saved data. This is not a
durable outbox: closing the application loses its pending request state.

New shared scenarios exercise all 13 methods through embedded and daemon clients,
including invalid request IDs/revisions, changed payload/command rejection,
exact replay after edits/deletion, restart and backup/restore. CLI integration
checks schema discoverability and stable-request replay in both modes. GUI
view-model tests use a real disposable SQLite backend behind a lost-response
proxy to cover unchanged retries, refresh failure after success, time-zone and
task-default changes, cancellation, and late responses. These are not live
daemon-backed GUI transport checks.

Verification: full solution build with zero warnings/errors, all 110 tests pass
(92 application, 18 domain), and `git diff --check` passes. Seeded minimum-size
980×640 screenshot checks passed for persisted editors and workspace operations
at `/tmp/snook-create-ui-final.hlOqcuNx`; empty captures are at
`/tmp/snook-create-ui-empty.jA598gcS`. Relevant page, drawer, conflict and empty
PNGs were inspected. The final error-message/UTC-normalization refinements were
verified by the full test run; those refinements did not change layout.

Still open: older update/lifecycle/timer retry behavior, uncertain creation when
refresh invalidates a selected parent, reconciliation of replayed entities later
deleted, saved GUI profiles and startup recovery, and actual daemon-backed GUI
interactions/outages. The earlier release-2 RPM contains contract 1.5 and must
be rebuilt/versioned before shipping this 1.6 work. Broader stream, maintenance,
platform and native package acceptance above remain required.

## Maintenance output safety pass

The path audit found that JSON/CSV exports could truncate any selected existing
file, including the workspace or token, and backup could overwrite an existing
database or manifest. All three output operations now require a fully qualified,
unused destination. Shared persistence validation rejects workspace/sidecar/
lease/recovery/credential names, case variants, traversal and device names, and
linked files/directories. Logical and resolved workspace names are reserved when
an embedded host was opened through a directory alias. Backup also refuses
pre-existing manifest, WAL, SHM, journal and owner companions.

Outputs are prepared in a private sibling staging directory, hashed before
publication, flushed, and moved with no replacement. Backup runs SQLite integrity
and foreign-key checks before publishing. Unix files are mode 0600 and staging
directories 0700. The manifest is published last; a failure between the two moves
can leave a database without a completion manifest. Cleanup only targets known
staging files, never final destinations or caller-selected directories. JSON/CSV
collection holds the shared writer gate so a write/restore cannot interleave
the separate export queries. These operations are not entity-receipt mutations.

Executable evidence covers embedded/daemon rejection of live and absent reserved
paths, malformed/relative/traversal paths, existing destinations and backup
companions, unchanged original bytes/credentials, private modes, hashes and
manifest contents. Additional tests cover symbolic/dangling links, directory
aliases, concurrent attempts at one destination, pre-cancellation, invalid CSV
ranges, and no published artifact after backup integrity failure. CLI scenarios
in both modes verify that repeat output calls return structured validation errors
without replacing the successful first artifact. Verification: full solution
build with zero warnings/errors, all 115 tests pass (97 application, 18 domain),
and `git diff --check` passes. Tests used disposable profiles with local
process/socket access. No UI layout or installed package changed in this pass.

Remaining maintenance acceptance is substantive. The verified-restore pass below
adds manifest/hash checking, source path validation, connection exclusion and
ordered restore invalidation. Crash-consistent activation/recovery still needs
an atomic outcome audit. The export schema-5 pass below closes the known field
omissions and adds concurrent task/tag snapshot checks; broader restore/fault
injection remains acceptance work. Maintenance
job bounds, power-loss directory-entry durability, abandoned-stage recovery,
Windows ACLs and native platform verification remain open. Destination directories
must be trusted; path checks alone do not defeat a hostile process replacing
ancestor directories between calls. No schema/contract signature changed here.

## JSON export schema-5 pass

The export now includes persisted workspace settings, its committed snapshot
cursor, task links and dependency edges. Relationship reads are not filtered by
parent visibility and retain stored IDs, revision and creation/deletion metadata.
Dedicated export reads also preserve stored deleted tags and tracking sessions,
including their intervals and correction history. The existing active-screen
queries remain unchanged. JSON is serialized to the private staging stream
without first allocating a second complete serialized string. The existing
shared writer gate keeps every section in the same mutation-free interval.

Schema 5 retains the previous section shapes and adds the above fields plus
`DeletedAtUtc` on exported tags/sessions. SQLite remains schema 10 and the backend
contract remains 1.6. `docs/json-export.md` documents field casing, numeric enums,
relationship direction, timestamps, snapshot cursor, retained deletion history,
privacy, schema compatibility and exclusions. This is not a JSON import/restore
format or a replacement for backup receipts/storage metadata.

New scenarios verify organization/tasks/settings/relationships, manual time and
corrections, calendar series/exception/planning data, habits and journal content
through embedded and daemon clients. SQL row comparisons verify relationship
metadata; separate fixtures cover stored link/tag/session tombstones and empty
relationships. Backup/restore and daemon-to-embedded restart preserve the
snapshot. A concurrent writer performs 40 atomic task/tag generations while 30
exports check that titles, tag assignments and cursor all describe one committed
generation. This is concurrency evidence, not power-loss fault injection.
CLI checks in both modes verify the result/file schema version and new sections.

Verification before packaging: full solution build with zero warnings/errors,
all 119 tests pass (101 application, 18 domain), `git diff --check` and shell
syntax checks pass. No UI layout changed. Release 3 packaging now includes the
format reference and refreshed payloads; package verification is recorded below.
Native install/upgrade/uninstall and the remaining production
acceptance scope are unchanged.

Release-3 artifact: `artifacts/rpm/snook-0.1.0-3.fc44.x86_64.rpm`, SHA-256
`80f10b30f7f3718a720dc33e8d98701e768ed1aff6133d759f2381c757f46d6c`.
Fresh package workspace: `.rpm-work/build.J5CdCwMs`. All three self-contained
payloads were republished from current sources. `verify-rpm.sh` passed actual
installed-path/wrapper/desktop-launcher metadata, all four packaged documents'
byte equality, native dependencies, absence of install scriptlets, help,
custom-port discovery, stable create replay, schema-5 export/hash/no-overwrite,
credential-path rejection, CLI watch, fail-closed ownership, graceful shutdown
and persisted embedded reopening. Extraction/evidence is at
`/tmp/snook-rpm-check.ZnGG1HAV`.

`verify-user-service.sh` passed using those exact payloads and shipped unit
properties: authenticated readiness, Type/UMask/NoNewPrivileges, private token,
restart persistence, redacted journal, stop and lease reopening. Evidence is at
`/tmp/snook-systemd-check.fI9asJ5D`; the disposable unit was stopped/collected.
No package or normal service was installed/enabled. Solution restore after
runtime-specific publishing, full build (zero warnings/errors), all 119 tests,
shell syntax checks and `git diff --check` passed. This refresh supersedes the
release-2 artifact for the new contract/export features, but does not establish
native package lifecycle, actual daemon-backed GUI interaction, Windows service,
or overall production acceptance.

## Verified restore and connection-lifetime pass

Normal restore now requires the backup's format-1 manifest. It checks bounded
JSON/fields, duplicate keys, exact byte count and SHA-256 of the private staged
copy before SQLite opens it. Source databases are not opened with SQLite and
never gain sidecars. Linked/unsafe paths and sources with WAL/SHM/journal/owner
companions are rejected. Backups/restores are limited to 16 GiB and manifests to
16 KiB; online backup checks page counts before copying. Moved valid pairs remain
supported because the manifest's original path is not followed. Historical-schema
fixtures now carry matching manifests; corrupt-schema fixtures recompute their
test manifest so schema tests are not accidentally satisfied by a hash mismatch.

Staged validation uses trusted-schema off, quick/FK checks, checked upgrades,
checkpointing and a flush. Result hashes/cursor/identity are captured before
activation. Restore queues one snapshot invalidation under the writer gate and
delivers it through the existing ordered observer-safe queue; the backend no
longer reloads state after replacement to construct a possibly newer notification.
Cancellation after commit and throwing/reentrant observers cannot convert the
committed result into failure or reorder the restore/new-mutation notifications.

Native connections now have explicit async lifetime leases through a shared
connection gate. Restore excludes reads as well as writes until its switch is
complete; this prevents a read from recreating SQLite in the missing-file window.
Disposal drains active connections before releasing workspace ownership and
lets queued callers wake and reject disposal rather than stranding their waits.
If startup finds no active database but restore archives/staging remain, it fails
closed instead of seeding a new empty workspace. Existing restore archives are
preserved; no historical migration SQL changed.

Verification: full solution build has zero warnings/errors; all 125 tests pass
(107 application, 18 domain), and `git diff --check` passes. Shared embedded/daemon
tests reject missing/malformed/tampered manifests, wrong lengths/hashes, valid-hash
invalid SQLite, sidecars and links while checking unchanged live bytes/cursor and
archives. Valid moved pairs restore with correct invalidation metadata. CLI tests
cover structured rejection and successful verified restore in both modes. Controlled
commit-pause tests prove reads wait and disposal retains ownership until restore
finishes. Other cases cover pre-cancellation, post-commit cancellation, throwing/
reentrant observers and fail-closed startup after a simulated missing-file window.
All profiles were disposable; no UI layout or installed package changed.

Still required: a crash-consistent activation journal/automatic recovery choice,
directory-entry durability and real process-kill/disk-failure tests; restored
active-timer reconciliation; richer backup manifest/status/preview support from
the specification; bounded long-running maintenance jobs; and broader SSE restore/
reconnect reconciliation. Windows ACL/native verification and GUI connection/
outage workflows remain open. Release 3 predates this restore pass and must be
rebuilt/versioned before these changes are shipped. Current RPC signatures and
SQLite schema remain 1.6 and 10; normal restore's manifest requirement is now
enforced rather than accepting an unverified bare database.

## Restored-timer reconciliation pass

Verified restore now reconciles nondeleted running sessions and sessions with open
intervals inside the staged candidate transaction. It preserves session/interval
identities and closed history, closes open intervals at the manifest backup time
(clamped to the interval start on a rolled-back backup clock), and marks the
session `RecoveryRequired` / `workspace-restored`. The candidate gains a mutation,
receipt and before/after correction for each prepared item. The single restore
invalidation uses that prepared candidate's cursor. Source backup bytes/manifest
remain immutable; the activated candidate's hash may therefore differ from them.
New backup metadata captures its clock boundary immediately before SQLite copying
under the writer gate, excluding later hashing/verification time. Startup recovery
detection now uses the store's injected clock as well.

Last known retains only frozen time. Stop now explicitly inserts a closed gap
interval. Continue starts a new open interval at the decision time without the gap,
validates referenced task/activity liveness and honors the normal foreground
concurrency preference/override. A current clock before the frozen boundary rejects
Stop now/Continue without changing state; Last known remains available. Every
explicit resolution records before/after correction provenance in the same
transaction as its revision change and exact receipt. Existing receipts remain
exact historical results, not instructions to put the current timer back into an
old running state. Already closed unresolved items survive a new backup/restore
without advancing the boundary or duplicating preparation provenance.

Dashboard cards now identify the task/activity, lane and start, show recorded time
and the precise last-known local timestamp/offset, and explain all three credit
policies. Text wraps and actions sit below it. Contextual automation names identify
the timer; unchanged rows retain controls/focus across refresh. The new opt-in
screenshot recovery fixture performs a real backup/restore in its disposable
profile. Its verifier clicks each recovery action and checks persisted state and
correction provenance, with focused-control survival across an unrelated refresh.
README, CLI guidance, operations and UI direction describe the behavior.

Verification: full solution build succeeds with zero warnings/errors; all 137 tests
pass (119 application, 18 domain), and `git diff --check` passes. Six shared scenarios
(three decisions × two lanes) run both through embedded and authenticated daemon
clients. They check frozen History/Summary durations, unchanged source pairs,
candidate cursor/hash, preserved interval identities, repeated unresolved restore,
exact start/resolution replay, and before/after provenance. Embedded restart tests
also prove an exact Running result does not reopen a subsequently stopped timer.
Additional tests cover rollback before restore and before backup, paused/stopped
preservation, both foreground concurrency settings, and archived/deleted activity
rejection without mutating the recovery item.

Inspected 980×640 evidence (disposable profiles only):

- Restored cards, three persisted actions and resolved tracker/history:
  `/tmp/snook-restore-ui-final.lWzv4fv3/images`.
- Normal seeded Dashboard/Tracker:
  `/tmp/snook-restore-ui-seeded.sDJFfu6M/images`.
- Empty Dashboard/Tracker:
  `/tmp/snook-restore-ui-empty.hei0Qtnz/images`.

This completes restored-timer freezing/three-way resolution, not all interruption
or daemon acceptance. The spec's direct **edit boundaries** recovery choice is
still missing (correction currently requires resolving first); zero-duration items
also need explicit History reachability. Audit older incomplete-row/clock-rollback
recovery, task parent/completion/dependency validation, and GUI lifecycle/recovery
retry retention separately. Next GUI work is a saved connection profile, a useful
daemon-mode launcher, visible configuration/startup/outage recovery and actual
daemon-backed GUI interactions; CLI doctor should report the resolved discovered
endpoint. Crash-consistent activation/recovery journals, directory durability,
kill/disk-failure tests, richer maintenance APIs/jobs, stream reconciliation and
native packaging/Windows acceptance remain required. Release 3 predates both
restore passes; no RPM or normal user service was installed or changed here.

## Saved client profile and GUI/CLI daemon operation pass

The GUI and CLI now share a strict version-1 device-local client profile. Its
launch location is selected before profile loading; command-line values override
environment values, which override saved values, with embedded mode used only
when no selection exists. The profile stores host mode, workspace data directory,
optional loopback endpoint and token-file path, never the bearer token. Strict
JSON/size/path/link/private-permission checks run before a workspace opens. Writes
use a private lock, optimistic content stamp, flushed private staging file and
atomic replacement. Invalid settings fail closed; CLI `--no-profile` is an
explicit recovery/bypass path and still requires an explicit host selection.

Desktop startup now uses a visible connection window instead of hiding daemon
authentication/connectivity failures during application construction. Users can
correct settings, retry, and save the intended next launch even while the daemon
is unavailable. The main window reports embedded mode or the actual daemon
endpoint and opens a settings-only connection dialog without switching the live
backend. A disconnected client raises a visible warning and manual refresh action;
the current draft stays owned by its editor and no offline writes are queued.
Background listener startup and shared asynchronous disposal no longer depend on
the Avalonia synchronization context. The desktop entry point supports `--configure`
and `--no-profile`, and the packaged desktop file exposes daemon and connection
settings actions.

CLI backend creation uses the same resolver and saved profile. A command can use a
GUI-saved daemon profile without host/endpoint/token flags, and `doctor` reports
the client's actual resolved/discovered endpoint. Neither client creates a local
SQLite database after daemon selection or a failed daemon connection.

Verification: full solution build passed with zero warnings/errors; all 149 tests
passed (131 application, 18 domain). New cases cover resolution precedence, strict
profile parsing and permissions, stale-save conflict, cancellation, missing-token
draft retention, settings-only persistence, pending-window cleanup, repeated
disposal on a non-pumping synchronization context, saved-profile CLI use and real
daemon disconnect state. An actual daemon-backed 980×640 run passed startup token
correction/profile saving, connection settings, task capture, timer pause/resume/
stop/start and task-list interactions with no client SQLite database. A separate
empty 980×640 run passed the connection settings checks. The final controlled
daemon stop/restart sequence then passed: the stale-data warning appeared, Retry
failed visibly while offline, the typed draft remained, and the same-host restart
refreshed without changing workspace identity. Both frames were inspected at
`/tmp/snook-connection-outage-final.UUlPWC1o/images`; the restarted daemon stopped
cleanly and released its lease.

Still open: deliberate live endpoint/token changes with draft preservation,
workspace-identity change safeguards, shutdown while a mutation is pending, and
the broader update/lifecycle/timer retry audit. Stream gap/overflow/restore ordering,
request saturation/slow-input cases, crash-safe restore activation, native package
lifecycle and Windows service/ACL verification remain the larger production gates.
The current consolidated view is [production-readiness.md](production-readiness.md).
