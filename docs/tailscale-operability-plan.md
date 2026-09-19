# Tailscale operability amendment plan

Status: functional implementation complete; live, platform, and security release acceptance outstanding
Scope: existing desktop client, CLI, and daemon  
Last reviewed: 2026-09-19

The implemented schema and security self-review are recorded in
[tailscale-threat-model.md](tailscale-threat-model.md). The historical baseline
below explains the motivation; current verified status is tracked in
[production-readiness.md](production-readiness.md).

## Current conclusion

Snook now implements the local pieces of the target design: a Tailscale Serve
profile over the daemon's loopback listener, short-lived pairing, per-device
revocable credentials, protected client storage, server/workspace identity
binding, conflict-safe Serve administration, diagnostics, and host/client UI.
The daemon still binds only to loopback, and the design neither exposes a LAN
listener nor accepts arbitrary HTTPS origins, adds synchronization, or uses
Tailscale Funnel.

This remains experimental rather than supported. The live two-node tailnet matrix,
native protected-store platform checks, second-desktop-OS CLI-shape check, native
RPM lifecycle, and independent security review in the release gates below have not
been completed. The local recovery bearer remains host-only and is not the normal
remote onboarding credential.

## Original repository baseline

| Area | Current behavior | Tailscale impact |
| --- | --- | --- |
| Daemon listener | `DaemonRpcServer` binds `http://127.0.0.1:<port>/` through `HttpListener`. | This is the correct upstream for Tailscale Serve and should remain unchanged. |
| Endpoint validation | `DaemonBackendClient.ValidateEndpoint` requires HTTP, loopback, root path, and no credentials/query/fragment. | Every `https://<machine>.<tailnet>.ts.net/` Serve URL is rejected. |
| HTTP policy | `HttpClientHandler` disables redirects and proxies. | Keep both protections; ordinary OS DNS and routing can still reach MagicDNS through Tailscale. Add normal TLS validation. |
| Authentication | One 256-bit bearer token is stored in `<data-root>/Snook/daemon.token` and sent as `X-Snook-Token` on health, RPC, and SSE. | A remote device cannot use default discovery and should not receive the daemon's local recovery credential. There is no enrollment, per-device revocation, or credential inventory. |
| Profiles | Version-1 profiles store host mode, data directory, optional endpoint, and optional token-file path; never a token. | The boundary is sound, but a tailnet profile needs an endpoint kind and a reference to a device-local secret rather than a path on the daemon host. |
| Discovery | `daemon.endpoint.json` is read from the selected daemon data directory. | Correct for same-host clients only. A remote client needs explicit or paired discovery and must not require access to server storage. |
| Desktop UI | The connection window says “Local daemon,” requests the daemon data root, and labels the endpoint “HTTP loopback only.” | There is no remote/tailnet onboarding, Tailscale-specific diagnosis, device naming, or credential management. |
| CLI and docs | CLI help, `README.md`, `docs/cli.md`, and `docs/operations.md` describe daemon transport as local-only; operations explicitly say remote/TLS hosting is not implemented. | Operators have no supported Serve setup, grants example, remote doctor flow, or recovery procedure. |
| Change stream | The daemon uses authenticated SSE with bounded subscribers, heartbeat, reconnect, and snapshot refresh. | The model should work through Serve, but streaming, roaming, daemon restart, and direct/DERP transitions need real acceptance tests. |
| Packaging | The Fedora package provides a hardened per-user `snookd` service but does not inspect or configure Tailscale Serve. | Setup must coexist with the user service, persist safely across reboot, and avoid overwriting unrelated Serve configuration. |

This is an amendment to the existing architecture, not a replacement for it.
`IBackendClient`, embedded mode, the one-workspace owner lease, transaction and
receipt semantics, and the no-offline-write/no-embedded-fallback rules remain.

## Target experience

### Reference topology

```text
Snook desktop or CLI
  -> operating-system Tailscale client
  -> tailnet policy/grant
  -> HTTPS https://snook-host.<tailnet>.ts.net/
  -> Tailscale Serve TLS termination
  -> HTTP http://127.0.0.1:<snookd-port>/
  -> authenticated snookd RPC, health, pairing, and SSE
  -> the daemon-owned SQLite workspace
```

The daemon never binds a Tailscale IP, LAN interface, wildcard address, or public
interface. Tailscale Serve is the only remote reverse proxy supported. Funnel,
router port forwarding, generic public reverse proxies, cleartext tailnet HTTP,
and raw `100.x`/IPv6 endpoint profiles are unsupported and rejected.

### First-time host setup

1. The operator installs and signs in to Tailscale on the host and enables
   MagicDNS/HTTPS for the tailnet.
2. The operator starts the existing per-user `snookd` service.
3. A Snook Tailscale setup command inspects daemon readiness, the installed
   Tailscale client, current node identity, and existing Serve configuration.
4. With explicit confirmation, it adds only the root HTTPS mapping from the
   node's `.ts.net` name to the current Snook loopback port in background mode.
   It refuses to replace an existing root mapping and never calls a broad
   `tailscale serve reset`.
5. The command prints the canonical Serve URL, a least-privilege grant template,
   and separate next steps for pairing a client. It never prints a bearer token.

The supported equivalent manual command should remain documented, for example:

```bash
tailscale serve --bg http://127.0.0.1:43871
```

The implementation must derive the actual port from the private daemon endpoint
descriptor and show the exact command before applying it. Do not assume port
`43871` when the daemon is configured differently. Since current Tailscale CLI
syntax and privileges can vary by platform/version, the helper must feature-detect
the installed CLI, parse bounded JSON status/config output where available, and
return an actionable manual command when automatic application is unavailable.

### First-time client pairing

1. In the already connected host UI, the user selects **Pair a tailnet device**,
   gives it a recognizable name, and creates a single-use code. CLI operators can
   perform the same action through a dedicated daemon-administration command.
2. Snook displays the HTTPS Serve URL, code, expiration, intended Tailscale login
   when one is selected, and a warning that both devices must be on the intended
   tailnet. Desktop clients use copy/paste; the same payload may later back the
   Android QR flow.
3. The remote desktop chooses **Tailnet daemon**, enters the Serve URL and pairing
   code, reviews the returned server/workspace identity, and confirms.
4. The daemon consumes the code once and issues a random per-install credential.
   The client stores it in device-local protected storage and saves only its
   credential reference, endpoint, server identity, and display name in the
   connection profile.
5. The client performs authenticated health and contract negotiation, then opens
   the normal shared Avalonia UI. It never creates a local SQLite workspace.

Pairing is transport bootstrap, not part of `IBackendClient`. Keep its bounded
routes and DTOs in the transport/application boundary so the domain and public
workspace contract do not acquire host-administration concepts.

## Required design decisions

### 1. Two explicit daemon endpoint policies

Replace the single loopback validator with a value object or policy-aware
validator that classifies endpoints as:

- `LocalLoopback`: `http://localhost:<port>/`, `http://127.0.0.1:<port>/`, or
  loopback IPv6, retaining today's exact restrictions.
- `TailscaleServe`: `https://<dns-name>.ts.net/`, using the fully qualified
  MagicDNS name, default HTTPS port, root path, and no user info, query, fragment,
  IDN ambiguity, or redirects.

Reject HTTP for remote names, non-`.ts.net` HTTPS names, literal IP addresses,
non-default ports in the initial release, wildcard/invalid DNS labels, trailing
path mounts, and any URI whose canonical reserialization changes its authority.
Resolve normally through the operating system so the installed Tailscale client
and MagicDNS remain responsible for routing. Do not embed `tsnet`, request auth
keys, call the Tailscale control-plane API from clients, disable certificate
validation, add certificate pinning, or add a custom CA bypass.

Keep redirects and HTTP proxies disabled so credentials cannot be forwarded to a
different authority. Build every health/RPC/SSE URI relative to the already
validated origin, and test that redirects, scheme downgrade, host confusion, DNS
failure, and TLS failure all fail closed.

### 2. Tailscale access plus Snook authentication

Treat the layers separately:

- Tailscale node identity, WireGuard transport, and grants decide which tailnet
  principals can reach the Serve endpoint.
- Normal Web PKI validation of the `.ts.net` certificate authenticates the HTTPS
  origin to the client.
- A Snook per-device credential authorizes access to the selected workspace and
  supports explicit revocation.

Continue authenticating health, RPC, and SSE. Do not treat tailnet membership by
itself as workspace authorization. Tailscale Serve adds user identity headers and
strips spoofed incoming copies, but those headers are absent for tagged source
devices. Record a bounded, normalized `Tailscale-User-Login` for diagnostics and
optional pairing restrictions when present; never make it the only application
credential. Do not log identity headers alongside task content or any credential.

The daemon's current `daemon.token` becomes a local recovery/administration
credential. Do not copy or display it during tailnet setup. Newly paired devices
receive separate random credentials. Store only salted/verifier forms server-side
in a private, bounded host-auth file beside the existing daemon metadata, not in
the workspace database, backups, exports, profile, or `IBackendClient` DTOs.
Define atomic write, owner-only permissions, symlink/reparse-point rejection,
maximum record count, maximum file size, corruption behavior, and recovery before
implementation. Authentication lookup and comparison remain fixed-time where
applicable.

Each credential record needs a stable ID, display name, creation time, optional
last-seen metadata, optional paired Tailscale login, scopes/version, and revoked
state. Initial scope is full workspace access; structure it so later read-only
credentials do not require another file-format break. The server returns a generic
unauthorized response for unknown, expired, or revoked credentials.

### 3. Pairing protocol

Add narrowly scoped pre-authentication endpoints under a separately versioned
transport namespace. A pairing session must be explicitly armed by an already
authenticated local administrator, expire within minutes using injected
`TimeProvider`, carry at least 128 bits of randomness even if a shorter human code
is displayed, be stored only as a verifier/in-memory state, and be consumed
atomically on first successful exchange.

Bound request size and concurrency independently from RPC. Rate-limit attempts by
pairing session and source signal without relying on source IP as identity. Return
no indication that distinguishes an unknown code from an expired or already-used
one. Cancel outstanding sessions on daemon shutdown and allow the local
administrator to revoke them early. Never enable pairing indefinitely.

The response returns the new device credential exactly once plus public server
metadata: canonical Serve origin, daemon instance/server ID, workspace identity,
contract version, and credential ID. The client must show the identity for user
confirmation before committing its profile. If confirmation is cancelled, revoke
the just-issued credential or complete a bounded cancellation flow; do not leave
unknown active devices.

Threat-model local hostile processes, observed/guessed codes, replay, concurrent
redemption, copied invitation text, Serve misconfiguration, missing identity
headers, wrong tailnet, profile substitution, and a compromised paired device.
Require an independent security review before declaring pairing release-ready.

### 4. Device-local credential storage and profile migration

Introduce an `IClientCredentialStore` owned by the application/platform layer.
Select and verify native adapters for Windows Credential Manager, macOS Keychain,
and Linux Secret Service. If a platform has no usable protected store, fail with
clear setup guidance or require an explicitly selected owner-only secret file;
never silently place a credential in JSON, command arguments, logs, clipboard
history, or general application preferences.

Version the client profile format. A tailnet profile stores:

- transport kind (`local-loopback` or `tailscale-serve`),
- canonical endpoint,
- credential-store reference (not secret),
- expected server/workspace identity,
- friendly server/device name,
- last successfully negotiated contract metadata.

Keep version-1 local profiles readable and migrate them without changing behavior.
Continue supporting explicit `--token-file` for local automation. For noninteractive
tailnet CLI use, support a protected credential reference or owner-only token file;
retain `SNOOK_DAEMON_TOKEN` only as a deliberately documented legacy/testing escape
hatch and warn against shell-history/environment leakage. Never place the pairing
code or issued credential in a saved profile.

### 5. Conflict-safe Tailscale Serve operations

Add a small composition-root adapter around the external `tailscale` executable;
do not introduce Tailscale dependencies into Domain, Contracts, Persistence, or
the shared backend. It should:

- locate a supported CLI without shell interpolation;
- impose execution deadlines and bound stdout/stderr;
- inspect `tailscale status --json` and Serve configuration/status;
- verify signed-in/running state, MagicDNS-resolvable FQDN, and HTTPS readiness;
- identify the current node name without accepting untrusted command output as a
  filesystem path or executable argument;
- preview the exact change;
- add only Snook's root reverse-proxy mapping after explicit user action;
- recognize an already-correct mapping as success;
- refuse conflicts instead of overwriting another application;
- disable only a mapping proven to match Snook's recorded origin and upstream;
- never enable Funnel, alter grants, enroll nodes, create auth keys, or reset all
  Serve configuration.

Persist no Tailscale login credential. The helper may report a grants snippet but
must not edit the tailnet policy. Treat Tailscale CLI absence, insufficient
privilege, logged-out state, HTTPS consent required, version mismatch, conflicting
Serve root, and daemon descriptor/readiness failure as different actionable
states.

### 6. Least-privilege grants and Funnel prohibition

Document a minimal tailnet grant whose source is the intended user/group/device
set and whose destination is only the Snook host's Serve HTTPS port. Validate the
example against the then-current Tailscale grants syntax during implementation;
do not ship a guessed policy fragment. Include a deny test using a second tailnet
node outside the permitted source set.

Every setup/status screen and operations page must say that Funnel is unsupported
and public. Detect a Funnel-owned/conflicting endpoint where the CLI exposes that
state and refuse to call it ready. Never suggest `tailscale funnel`, a public DNS
name, a LAN bind, or a firewall/router change as troubleshooting.

### 7. Desktop UX and diagnostics

Revise the connection window into three clear choices:

- **Embedded workspace** — this process owns local SQLite.
- **Local daemon** — same-machine loopback discovery and token-file behavior.
- **Tailnet daemon** — HTTPS Serve URL plus pair/connect/revoke workflow.

For tailnet mode, do not ask for the server's data directory. Show server name,
workspace identity, Tailscale URL, last connection state, and credential label.
Provide paste-friendly onboarding and reserve the same invitation payload format
for Android QR scanning. Clearly state that the user must install/sign in to the
Tailscale client separately; Snook does not control the VPN.

Map failures to actionable messages without pretending Snook can directly inspect
every VPN state: invalid/non-Tailscale URL, name not resolved, connection refused,
TLS/certificate failure, HTTP timeout, grant/policy denial, pairing expired,
authentication revoked, incompatible contract, daemon stopped, and SSE reconnecting.
Use cautious wording when several causes produce the same socket error. Preserve
draft ownership and stale-data behavior during outages, and never offer embedded
fallback from an attached profile.

Extend `doctor` with structured, secret-free fields for endpoint kind, canonical
origin, DNS/TLS/connectivity/auth/contract stages, expected versus actual server
identity, and stream readiness. Exit nonzero on any required stage. A diagnostic
bundle must redact authorization headers, pairing material, usernames unless the
user explicitly opts in, database paths, and workspace content.

### 8. Lifecycle and reliability

Keep the current snapshot-after-reconnect model; Tailscale support is not sync and
does not add an offline queue. Validate all of these transitions:

- direct WireGuard path to DERP relay and back;
- Wi-Fi/Ethernet network change and temporary loss;
- Tailscale disconnected/reconnected on the client;
- daemon stop/restart behind a persistent Serve mapping;
- Tailscale daemon stop/restart with `serve --bg` restoration;
- DNS temporarily unavailable;
- client node, user, grant, or Snook credential revoked;
- server node renamed (certificate/URL changes) or removed from the tailnet;
- host suspend/resume and desktop client suspend/resume;
- a long-lived SSE connection crossing transient network failure.

Reads may become stale and writes may have uncertain outcomes. Preserve existing
operation-ID and exact-receipt guidance; do not automatically replay mutations.
On reconnect, authenticate and negotiate again, verify the expected server/workspace
identity, fetch a fresh bootstrap snapshot, and only then resume SSE.

## Proposed code ownership

- `src/Snook.Application`: endpoint policy/value type, transport factory, pairing
  client, credential-store abstraction, profile v2 and migration, diagnostics.
- `src/Snook.Daemon`: credential registry, pairing/admin HTTP routes, request
  authentication policy, Tailscale identity metadata handling, and bounded host
  setup/status commands.
- `src/Snook.UI`: three-mode connection experience, pairing confirmation, device
  inventory/revocation, and Tailscale-specific recovery states.
- `src/Snook.Desktop`: platform credential-store selection and Tailscale setup
  composition only; keep shared UI platform-neutral.
- `src/Snook.Cli`: pair/admin/status/doctor surfaces and machine-readable results;
  workspace calls continue to derive from `IBackendClient`.
- `tests/Snook.Application.Tests`: endpoint, profile, auth, pairing, transport,
  daemon process, CLI, migration, and reconnect coverage.
- `src/Snook.Screenshot` and scripts: disposable tailnet-mode UI states using fake
  transports for deterministic screenshots; never use a real workspace.
- packaging/scripts: opt-in host setup verification without installing, enrolling,
  or changing Tailscale during RPM installation.

Do not add Tailscale concepts to `Snook.Domain`, workspace migrations, backups, or
exports. Pairing/admin endpoints are not `IBackendClient` methods, so this work
does not by itself require a workspace contract version bump. If implementation
changes the existing RPC envelope or shared contract, update `ContractInfo`, both
backend implementations, the daemon allowlist/catalog behavior, and contract
coverage together as required by the repository invariants.

## Implementation phases

### Phase 0 — spikes and threat model

1. Capture the exact supported Tailscale versions/platforms and confirm current
   `serve`, `status --json`, and config JSON shapes on Fedora and at least one other
   desktop OS.
2. Prove authenticated health, RPC, and sustained SSE through Serve without code
   changes by using a diagnostic client that does not weaken production endpoint
   validation.
3. Decide and prototype native credential stores on the supported desktop targets.
4. Write the pairing/auth/Serve threat model and review it before persisting a new
   secret format.
5. Freeze the profile-v2, host-auth-file, invitation, and pairing protocol schemas
   with explicit byte/count/time limits and forward-version failure behavior.

Exit: technical assumptions are demonstrated, secret storage is selected, and no
unresolved design requires weakening loopback binding or TLS validation.

### Phase 1 — safe HTTPS transport

1. Add endpoint classification and strict Tailscale Serve validation.
2. Refactor `DaemonBackendClient` creation behind a transport factory while keeping
   redirects/proxies disabled and standard TLS validation enabled.
3. Run health, RPC, SSE, cancellation, timeout, contract negotiation, and snapshot
   recovery tests against loopback HTTP and test HTTPS origins.
4. Add profile-v2 parsing/migration and preserve all existing precedence/fail-closed
   behavior.

Exit: an explicitly provisioned private credential can operate the current daemon
through an approved `.ts.net` Serve URL, while all disallowed origins fail closed.
This is a developer milestone, not yet the user-facing release.

### Phase 2 — per-device authentication and pairing

1. Implement the private credential registry and migration that preserves the
   existing local recovery token.
2. Add authenticated local device-list/revoke/pairing-session administration.
3. Add the short-lived one-time pairing exchange and client-side protected storage.
4. Bind profiles to expected server/workspace identity and handle cancel/revoke.
5. Add concurrency, replay, expiry, restart, corruption, permission, and redaction
   tests with injected time and randomness seams.

Exit: no normal remote setup requires copying `daemon.token`, each client can be
revoked independently, and pairing has passed security review.

### Phase 3 — easy host and desktop workflows

1. Implement conflict-safe Tailscale status/setup/disable inspection.
2. Add the three-mode desktop connection UI and host-side pairing/device management.
3. Add CLI parity and structured `doctor` diagnostics.
4. Add accessible focus, keyboard, automation-name, narrow-window, error, and
   screenshot coverage under `docs/ui-design.md` rules.
5. Keep manual commands available and ensure automation can configure a client
   without placing secrets on its command line.

Exit: a new user with Tailscale already installed can expose a running daemon and
pair another desktop without manually copying a bearer token or editing JSON.

### Phase 4 — operations, packaging, and live-tailnet acceptance

1. Add an opt-in packaging verification path for `snookd` plus persistent Serve;
   package installation itself must not change Tailscale state.
2. Document service ordering, logout/linger implications, Serve persistence,
   upgrades, node rename, credential rotation/revocation, conflict recovery, and
   complete removal.
3. Test an allowed and denied client on a disposable tailnet, then exercise direct
   and DERP paths, network roaming, daemon/Tailscale restart, SSE recovery, expired
   nodes, grant removal, and credential revocation.
4. Run the full repository build/test/diff-check sequence and the required desktop,
   CLI-through-saved-profile, daemon, and screenshot acceptance flows with only
   disposable workspaces.

Exit: setup survives restart, failures are diagnosable, policy denial is proven,
Funnel is absent, and both local daemon and embedded behavior remain unchanged.

## Verification matrix

### Automated

- Endpoint validation accepts only canonical loopback HTTP and canonical `.ts.net`
  HTTPS roots for their respective profile kinds.
- Redirects, proxies, HTTP downgrade, literal IPs, public/custom domains, credentials
  in URLs, paths, query/fragment, bad ports, malformed IDNs, and certificate errors
  are rejected without sending an authorization header elsewhere.
- Version-1 local profiles retain identical resolution precedence; malformed or
  unsafe version-2 profiles fail closed and never open SQLite.
- Pairing codes expire, revoke, rate-limit, and redeem exactly once under races.
- Credential registry creation/update is atomic, private, bounded, non-link-following,
  and deterministic on corrupt or future-version data.
- Local recovery and per-device tokens authenticate health/RPC/SSE as designed;
  revoked/unknown tokens fail identically and do not leak metadata.
- Tailscale identity headers are normalized/bounded, never trusted when policy says
  absent, and never able to bypass the Snook credential.
- Serve configuration parsing is bounded; correct state is idempotent, conflicts
  refuse mutation, and disable cannot remove another application's mapping.
- Reconnect performs identity/contract/auth negotiation and a fresh snapshot before
  resubscribing; uncertain mutations are not automatically replayed.
- Logs, JSON errors, doctor output, profiles, screenshots, crash paths, and process
  arguments contain no credential or pairing secret.

### Live tailnet

- Fedora host using packaged per-user daemon and persistent HTTPS Serve mapping.
- Desktop client on another node connects using the full `.ts.net` name and a
  paired credential; it has no server data-directory access and creates no SQLite
  workspace.
- Full bootstrap, representative reads/writes, backup/export/restore calls, and
  sustained change notifications work through Serve.
- A node inside the grant succeeds; a tailnet node outside it is denied.
- Public internet, ordinary LAN address, raw Tailscale IP, HTTP MagicDNS, Funnel,
  and an unrelated HTTPS origin cannot be used as daemon profiles.
- Revoking the Snook device credential stops that client without disrupting others;
  revoking/removing the Tailscale node or grant also stops it.
- Direct/DERP change, client network change, host/client suspend, daemon restart,
  and Tailscale restart recover without draft loss, SQLite fallback, or duplicate
  writes.

## Documentation amendments during implementation

Update these existing documents only when the corresponding behavior is built and
verified:

- `README.md`: supported topology, quick start, explicit Funnel prohibition, and
  links to operations/CLI details.
- `docs/operations.md`: Serve setup/status/removal, grants, pairing and revocation,
  systemd interaction, diagnostics, threat boundaries, and recovery.
- `docs/cli.md`: endpoint kinds, profile v2, credential references, pair/admin/doctor
  commands, machine-readable errors, and safe automation.
- `docs/ui-design.md`: local versus tailnet connection language, pairing/device
  management, recovery states, accessibility, and narrow-window behavior.
- `docs/progress.md` and `docs/production-readiness.md`: only verified implementation
  and live-tailnet evidence, with remaining platform/security limitations explicit.
- `docs/android-plan.md`: align its attached-mode transport, profile, pairing, and
  revocation requirements with the implemented shared design rather than creating
  a second mobile-only protocol.

## Release gates

Do not describe desktop/daemon Tailscale operation as supported until all are true:

- `snookd` still binds only to loopback and no Snook code enables Funnel or a LAN
  listener.
- A canonical `.ts.net` HTTPS profile completes authenticated health, contract
  negotiation, RPC, and SSE through Tailscale Serve.
- Normal onboarding uses short-lived pairing and a revocable per-device credential;
  the local recovery token is not copied to clients.
- Profiles and output contain no secret, and protected storage is verified on every
  claimed desktop platform.
- An explicit grant example is validated, an allowed node succeeds, and a denied
  node fails.
- Redirect, proxy, DNS, TLS, wrong-tailnet, wrong-server, revoked-node, revoked-token,
  restart, roaming, and uncertain-write behavior is tested and fail-closed.
- Existing embedded and local-loopback daemon acceptance remains green, including
  saved-profile CLI use, GUI reconnect, missing credentials, draft retention,
  daemon stop/restart, and absence of client SQLite in daemon mode.
- The full build, full tests, `git diff --check`, UI screenshots, RPM inspection,
  user-service verification, and a documented disposable live-tailnet run pass.

## Explicit non-goals

- Peer/cloud synchronization, offline mutation queues, or a second client database.
- Public hosting, Funnel, arbitrary reverse proxies, custom domains, custom CAs, or
  raw tailnet-IP profiles.
- Embedding or administering the Tailscale client, obtaining reusable auth keys,
  editing tailnet policy, or silently enrolling a machine.
- Replacing Snook authentication with identity headers alone.
- Moving SQLite ownership away from the daemon or weakening the workspace lease.
- Adding Tailscale-specific types to the domain or versioned workspace contract.

## Primary references

- [Tailscale Serve](https://tailscale.com/docs/features/tailscale-serve)
- [Tailscale Serve command](https://tailscale.com/docs/reference/tailscale-cli/serve)
- [Tailscale Serve examples](https://tailscale.com/docs/reference/examples/serve)
- [Tailscale MagicDNS](https://tailscale.com/docs/features/magicdns)
- [Tailscale identity](https://tailscale.com/docs/concepts/tailscale-identity)
- [Tailscale grants](https://tailscale.com/docs/features/access-control/grants)
- [Tailscale grants syntax](https://tailscale.com/docs/reference/syntax/grants)

The Serve CLI and grants example were rechecked on 2026-09-19 against the official
current references: `serve status --json` remains documented, and grants require
`src`, `dst`, plus `ip` and/or `app`; `tcp:443` is a documented network capability.
The helper does not depend on human display text when a bounded machine-readable
interface is available.
