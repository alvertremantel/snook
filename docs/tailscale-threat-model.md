# Tailscale Serve transport threat model

Status: implementation self-review; independent security review and live-tailnet
acceptance are still required
Last reviewed: 2026-09-19

This document freezes the first implemented transport schemas and records the
security assumptions behind Snook's experimental Tailscale Serve path. It is not a
release approval. The release gates remain in
[tailscale-operability-plan.md](tailscale-operability-plan.md).

## Trust boundaries and assets

The protected assets are workspace contents, mutation authority, the local recovery
credential, per-device credentials, pairing codes, and stable server/workspace
identity. SQLite remains owned only by `snookd`; no remote client gets a copy.

The boundary is layered:

1. Tailscale and its grants decide which nodes can reach port 443.
2. Normal Web PKI validates the canonical `.ts.net` origin.
3. Snook authenticates every health, RPC, and SSE request with an application
   credential.
4. The saved profile pins server and workspace IDs before RPC is allowed.

Tailscale identity headers are optional diagnostics and an invitation restriction.
They never replace the Snook credential. Tagged devices may have no user-login
header. The daemon normalizes and bounds a supplied login, but a local process can
forge a loopback header; possession of the one-time code and then the issued Snook
credential therefore remains the authorization boundary.

## Frozen version-1 transport schemas and bounds

These are device-local/transport formats, not `IBackendClient`, Domain, workspace,
backup, or export schemas.

### Client profile version 2

- Maximum serialized profile: 16 KiB; unknown JSON members and duplicate names are
  rejected.
- `host` is `daemon`; `transportKind` is `local-loopback` or `tailscale-serve`.
- A tailnet origin is exactly canonical
  `https://<machine>.<tailnet>.ts.net/` on port 443.
- A local origin accepts only `localhost`, the exact IPv4 literal `127.0.0.1`, or
  IPv6 `::1`; numeric shorthand and the rest of IPv4 `127/8` are rejected.
- A tailnet profile has exactly one credential source: an opaque native-store
  reference of at most 256 characters, or an absolute explicit owner-only token
  file. It contains no credential.
- Nonempty stable server and workspace GUIDs are mandatory. Friendly name is at
  most 100 characters; contract components are nonnegative.
- Version-1 local profiles retain their existing precedence and semantics.

### Host authentication file version 1

- Private `Snook/daemon-auth.json`; 256 KiB maximum, 256 records maximum, atomic
  write/replace, owner-only mode on Unix, no linked file or linked ancestor.
- Unknown fields, future versions, duplicate property names or IDs, null/missing
  structures, malformed salts/verifiers/timestamps, invalid names/logins/scopes,
  unsafe permissions, and oversized files fail daemon startup closed.
- At capacity, enrollment refuses to displace an active credential. The next
  enrollment may reclaim the oldest revoked or expired provisional record; the
  registry is bounded state, not an audit log.
- The file contains a stable server GUID and records with credential GUID, display
  name, UTC creation/last-seen instants, optional normalized login, scope array,
  revoked state, optional provisional-expiry instant, a random 256-bit salt, and
  a SHA-256 verifier.
- Device credentials are random 256-bit values, not passwords. Salted SHA-256 is
  used for fixed-time lookup; raw credentials are never persisted. The separate
  `daemon.token` remains the local recovery/administration credential.

### Pairing transport version 1

- Routes are under `/transport/v1`; administration requires the local recovery
  credential and is accepted only by clients configured for loopback.
- At most 16 in-memory invitations exist. Each code has 128 random bits, expires
  after five minutes, is stored only through a SHA-256 lookup key, and is consumed
  atomically by the first successful redemption.
- A session and a bounded source signal each permit eight failed attempts in a
  five-minute window. Source bookkeeping is capped at 128 entries. Pair redemption
  has a separate concurrency limit of four.
- Administration/pair request bodies are capped at 16 KiB or 4 KiB by route. The
  client accepts at most 256 KiB of response content and validates origin, IDs,
  contract, names, UTC timestamps, unique inventory IDs, normalized logins, bounded
  scopes, and credential before saving anything. Both server request DTOs and client
  responses reject unknown members and case-insensitive duplicate field names.
- Every JSON response, including the one-time credential exchange and errors,
  carries `Cache-Control: no-store`; the SSE stream does as well.
- The server emits a heartbeat every ten seconds and bounds each write to five
  seconds. The client treats 30 seconds without any SSE line as a failed stream,
  renegotiates, and refreshes a bootstrap snapshot before resubscribing.
- Unknown, expired, cancelled, replayed, wrong-login, and rate-limited invitations
  use one denial response. An administrator can cancel an armed invitation before
  expiry, and shutdown clears every outstanding invitation.
- If delivery of a successful redemption response fails, the daemon revokes the
  newly issued credential so a client cannot leave an unknown active credential.
- Confirmation first uses the issued credential for authenticated health/contract/
  identity negotiation against the reviewed server. A newly issued credential is
  durably provisional for five minutes. Health and self-cancel leave it provisional;
  the official client does not send the first RPC/SSE request that promotes the
  record until after the user confirms identity, the credential is protected, and
  the profile is atomically saved. An unused provisional record expires fail-closed across
  daemon restart. Failure before profile publication deletes the local copy and
  attempts bounded self-revocation; the host inventory shows the provisional expiry
  so an operator can also revoke it explicitly.

### Serve setup receipt version 1

- Private `Snook/tailscale-serve.json`, 8 KiB maximum, with only the canonical
  `.ts.net` origin and exact HTTP loopback upstream.
- The receipt uses a fixed version-1 schema with a depth bound and rejects duplicate,
  unknown, future-version, mistyped, noncanonical, unsafe-permission, and linked data.
  Publication is private and atomic.
- Setup inspects bounded `tailscale status --json` and `serve status --json`, refuses
  Funnel and occupied roots, previews argv without a shell, applies only after an
  explicit request, and verifies the result.
- Disable proceeds only when the live origin/upstream exactly match the receipt and
  removes only `/`. No command resets Serve, edits grants, enrolls a node, creates
  an auth key, or enables HTTPS consent/Funnel.

## Threats and implemented controls

| Threat | Control | Remaining acceptance |
| --- | --- | --- |
| Public/LAN/raw-IP/custom-domain exposure | `snookd` binds `127.0.0.1`; profiles accept only loopback HTTP or canonical `.ts.net` HTTPS; Funnel is refused. | Prove public, LAN, raw-IP, unrelated-origin, and denied-node failure on a disposable tailnet. |
| Credential forwarding by redirect/proxy | Redirects and HTTP proxies are disabled; every request URI derives from the validated root. DNS, TLS, and connectivity failures retain distinct cautious guidance without a bypass. | Exercise real proxy, redirect, DNS, and TLS failures. |
| Copied recovery token | Normal pairing issues a separate device credential; UI/setup never displays `daemon.token`. | Independent log/crash/process inspection on every platform. |
| Offline theft of host registry | Only random-token verifiers and salts are stored in a private bounded file outside workspace backups/exports. | Native Windows ACL and incident-recovery review. |
| Guessing/replaying a pairing code | 128 random bits, five-minute lifetime, bounded per-session, per-source and global attempts plus bounded source memory/concurrency, atomic one-use redemption only after code/login/normalized device name match, generic denial. Official clients use issued credentials only for health until durable confirmation; an otherwise unused provisional record expires. | Independent pairing/auth review. |
| Forged Tailscale identity header | Header never authorizes a request; a Snook credential is always required. Optional login restriction is defense in depth. | Validate actual Serve header stripping and tagged-node behavior. |
| Profile substitution/wrong workspace | Saved expected server/workspace IDs are compared during health negotiation before RPC and again against bootstrap. Health/pairing read current workspace identity; restore results and notifications invalidate negotiation. | Live wrong-tailnet, node rename, restore, and server replacement exercises through Serve. |
| Compromised paired device | Separate revocable credential; unknown/revoked credentials receive the same denial on health/RPC/SSE, and revocation completes every active SSE subscriber bound to that credential with a registration-race recheck. | Live revocation through Serve during sustained SSE and uncertain writes. |
| Serve configuration collision | Inspect, refuse conflicts, use exact argv, durably validate/write the exact ownership receipt before mutation, and verify the same origin/upstream after apply/disable. | A concurrent external administrator can still race the Tailscale CLI; operational changes require exclusive review of the preview and post-state. |
| Network loss after an uncertain write | Existing operation IDs/exact receipts remain authoritative; no transport layer automatically replays a mutation. | Direct/DERP, roaming, suspend, and restart acceptance. |

## Version/platform evidence

The Fedora 44 development host has Tailscale CLI `1.102.4` (`linux-x64`). Its
2026-09-19 help output confirms `serve <target>`, `serve status --json`, `--bg`,
`--https`, and `--set-path`, plus newer service-oriented commands that Snook does
not use. Parser fixtures cover the node and root-handler JSON shapes expected by
the adapter. A read-only live query confirmed a running, logged-in node with
MagicDNS and HTTPS enabled and an empty Serve configuration; Snook reported
`ready-to-apply` with the exact loopback upstream. The opt-in repository verifier
also completed its read-only identity/Serve and secret-redaction checks; neither
operation mutated Serve. Applying
Serve and the allowed/denied multi-node matrix remain unperformed. No second
desktop operating system has been accepted yet.

Native adapters exist for Linux Secret Service (`secret-tool`, with credentials on
stdin), Windows Credential Manager, and macOS Keychain. Only fake-process and
cross-platform compile tests are current evidence; each claimed platform still
needs real locked/unlocked/store/read/delete/cancel acceptance before release.

## Review disposition

Automated self-review covers endpoint confusion, redirect/TLS failure, profile
precedence, random seams, pairing expiry/replay/races/rate limits/early cancellation,
failed-response revocation, strict client response validation, registry restart/
corruption/permissions/links/atomic writes, generic auth denial, strict Serve state
and receipt parsing, Serve conflicts, credential redaction, workspace-content-free
doctor output, duplicate-safe bounded health/RPC envelopes, live-code-redacted
disposable UI captures, and RPM flows. This does not
satisfy the required independent security review or live-tailnet matrix.
