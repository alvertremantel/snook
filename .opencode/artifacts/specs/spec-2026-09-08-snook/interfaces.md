# Interfaces and Contracts

## Contract principle

The UI, CLI, embedded host, and daemon communicate in terms of versioned
application commands, queries, snapshots, and change notifications. They do not
share repositories, SQL DTOs, or transport-specific errors.

`IBackendClient` is the client-side seam. Two production implementations are
required:

- `InProcessBackendClient` — direct application dispatcher.
- `GrpcBackendClient` — local or remote daemon transport.

Contract tests feed identical scenarios to both and compare semantic results.

## Request envelope

Every mutation contains:

```text
operation_id        UUIDv7; stable across retries
contract_version    major/minor
client_device_id    authenticated identity or local host identity
expected_revision   aggregate revision when updating existing state
requested_at_utc    diagnostic only; backend clock is authoritative
payload             typed command
```

Backend returns:

```text
operation_id
status              applied | already_applied | conflict | rejected
aggregate_id?
new_revision?
committed_cursor?
result_payload?
error?
```

Errors are structured and localizable by the client:

- `validation_failed`
- `not_found`
- `deleted_or_archived`
- `invalid_transition`
- `revision_conflict`
- `dependency_blocked`
- `concurrency_policy_blocked`
- `store_busy`
- `store_unavailable`
- `schema_incompatible`
- `permission_denied`
- `capability_unavailable`
- `protocol_incompatible`
- `internal_error` with correlation ID only

No server traceback, SQL, file path, token, or user-content payload is returned
in a generic error.

## Application command groups

### Tasks/projects

- Create/update/archive/restore/delete board or project.
- Create/update/move/complete/reopen/delete/restore task.
- Add/remove dependency, tag, or link.
- Start tracking from task.

### Tracking

- Start session.
- Pause/resume/stop session with expected revision.
- Create manual session.
- Edit attribution/notes.
- Correct intervals with before/after preview and reason.
- Resolve recovery-required session.

### Calendar

- Create/update/delete calendar, event, series, exception, schedule block.
- Schedule/unschedule task independently of deadline.
- Start tracking from schedule block.

### Data and host

- Create/list/verify backup; preview/perform restore.
- Create/list/verify export job.
- Acquire current capabilities/status.
- Configure host-safe settings.
- Pair/revoke device and manage conflicts when sync is enabled.

Commands are purpose-specific. A generic `UpdateRow(table, values)` API is
forbidden because it bypasses invariants like task completion and timer state.

## Query groups

- `GetBootstrapSnapshot` — profile, capabilities, active sessions, Today summary,
  and committed change cursor needed to initialize a client.
- `GetToday`, `SearchTasks`, `GetBoard`, `GetTaskDetail`, `GetProjectDetail`.
- `SearchActivities`, `GetTrackingHistory`, `GetTrackingSummary`.
- `GetCalendarRange`, `GetScheduleAgenda`.
- `GetConflicts`, `GetDevices`, `GetSyncStatus` when available.
- `GetDataStatus`, `GetBackupStatus`, `GetExportStatus`.

Lists use opaque cursor pagination and caller bounds. Filters are typed. Range
queries use half-open `[start,end)` semantics. One query captures “now” once and
returns it so the client can render a coherent snapshot.

## Change stream

Clients subscribe from a committed mutation cursor. Notification contains only
what is needed to reconcile state, for example:

```text
cursor
operation_id
aggregate_type
aggregate_id
change_kind
new_revision
projection_hints[]
committed_at_utc
```

It does not broadcast full task notes or session content by default. Ordered
delivery is guaranteed per backend connection. On cursor expiry/gap the server
returns `snapshot_required`; the client reloads one bounded snapshot.

Active timer labels use snapshot boundary values and an in-memory timer. The
change stream reports lifecycle transitions; it emits no per-second event.

## Transport profiles

### In-process

Direct async calls, same cancellation and result types, no serialization. The
adapter must still honor idempotency and contract semantics; it may not expose
extra shortcuts to view-models.

### Local desktop IPC

- Windows: Kestrel gRPC over named pipes with current-user ACL.
- Linux: Kestrel gRPC over Unix domain socket in a user-runtime directory with
  owner-only permissions.
- Endpoint names include profile identity but no user content.
- Peer OS identity is checked where available.
- If loopback TCP fallback is ever enabled, it binds only loopback and requires
  a random bearer credential stored with owner-only protection.

### Remote daemon

- gRPC over HTTP/2 TLS on an explicitly configured listener.
- Mutual device authentication after short-lived pairing approval.
- Certificate/public-key pinning and revocation.
- Bounded requests, deadlines, cancellation, keepalive policy, rate limits, and
  audit events.
- Local IPC credentials are never accepted remotely.

### Android

In-process adapter is the embedded baseline; remote adapter uses standard TLS
HTTP/2. Android local sockets/custom gRPC are not required. Android Keystore
protects device credentials.

## Version and capability negotiation

- Contract version uses major/minor. Major mismatch fails; minor additions are
  backward compatible when fields are optional.
- `GetCapabilities` reports features such as tracking correction, recurrence,
  backups, export, sync, conflicts, daemon service management, and at-rest
  encryption.
- Clients hide/disable unsupported actions with an explanation.
- Data schema version is not the API version and is never negotiated by clients.
- Remote clients have a defined minimum-supported contract window before daemon
  upgrades are shipped.

## Daemon ownership and discovery

Client profiles explicitly select embedded, local-daemon, or remote-daemon.
Local endpoint discovery reads a protected endpoint descriptor containing only
transport path, daemon instance ID, contract version, and certificate/token
reference. It does not scan ports or use unauthenticated mDNS.

If a daemon-only client cannot connect, it offers retry, diagnostics, or profile
switch confirmation. It never opens the known DB path automatically.

## Future sync protocol boundary

Peer replication does not reuse the client command API blindly. It has a
separate authenticated service for capability exchange, mutation batches,
acknowledgements, snapshots, and conflict outcomes. Shared message foundations
(IDs, errors, limits, cryptographic identity) may be reused. Sync protocol detail
requires its own threat-modelled spec before implementation.
