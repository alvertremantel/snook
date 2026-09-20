# Android attachment threat model

The pairing, credential and fail-closed transport boundary described here now has a
development implementation. Existing desktop loopback authentication remains
unchanged. Attached Android must not ship until the adversarial acceptance cases in
`android-plan.md` pass independent review on an actual Tailscale Serve route.

Phase 0 now has [Keystore and receiver feasibility evidence](android-native-spike.md).
The synthetic probe verifies native encryption, authentication failures, removed-key
read failure and immutable non-exported receiver activation. The attached profile
now uses a separate Keystore alias and crash-safe credential-id envelope selection;
the profile codec cannot contain the credential. Hardware backing and real tailnet-
route acceptance remain unverified. Optional app lock gates the foreground activity
with the enrolled device credential; it is not database-at-rest encryption.

## Assets and trust

Protect workspace content, exact mutation receipts, server/workspace identity,
per-install credentials, pairing challenges, private drafts and backup contents.
Treat links, intents, QR/manual codes, profiles, storage providers, network input,
cached projections, and every unauthenticated caller as untrusted. A tailnet member
is not automatically authorized for a workspace.

The Android application process owns the selected workspace session. Activities
own presentation only. Native adapters obtain a session identity and a bounded
committed projection, never a raw store or an alternative backend. Switching host
invalidates action nonces, notifications and derivative caches before opening the
next session. App lock closes access to credentials and private native surfaces.

The development activity now sends every cold/warm launch through one shared
fail-closed router after extracting primitive intent metadata. Normal launch accepts
no data or extras. Shares accept only `SEND`, exact `text/plain`, one bounded text
extra and no URI, stream, ClipData, token or unknown key. Timer content accepts only
the exact internal action and `snook-internal://timer/<token>` route with one matching
64-character uppercase-hex token; the encrypted action store and current backend
state remain a second authorization gate. Unknown actions, malformed parcels and
payload substitution are rejected without mutation or draft replacement. No URI is
followed and no caller-owned stream or parcelable payload is consumed.

## Transport boundary and unresolved proof obligations

The only candidate remote origin is HTTPS on an explicitly configured MagicDNS
Serve host. URL validation must reject user-info, IP literals, non-HTTPS schemes,
unexpected ports, paths, queries and fragments; suffix matching must respect DNS
label boundaries. Disable proxies and redirects at the shared HTTP transport.
Use normal certificate validation. Every request, including health and SSE, must
use this same origin and application authorization.

The shared client now has separate endpoint policies rather than weakening its
desktop rule. Desktop/CLI remain restricted to HTTP loopback. The tailnet policy
accepts only an HTTPS DNS origin below the `.ts.net` label boundary, on port 443,
with no user-info, path, query or fragment; IP literals, lookalike suffixes, HTTP
and alternate ports fail before a credential can be sent. The production HTTP
handler disables redirects and system proxies. `WorkspaceSession` can now publish a
capability-checked `ReadyAttached` borrower, reports initial connection failure as
retryable `StaleAttached`, and proves that its independent factory is not called as
a fallback. The Android UI now accepts the constrained origin and manual code, shows
the observed identities before confirmation, protects the returned device credential
and opens a capability-checked attached session. The route/identity proof below still
blocks a release claim, not continued development testing.

A `*.ts.net` name and valid certificate alone do **not** prove tailnet-only access.
Serve and Funnel can publish the same host, and the host administrator can change
publication after pairing. Consequently a hostname allowlist alone cannot meet
the plan's Funnel prohibition. The feasibility test must prove the client route is
through Tailscale, and the server must reject remote application credentials when
trusted Serve identity is absent or differs from enrollment. Establishing and
recording that route proof remains an external acceptance obligation; a successful
HTTPS request alone is insufficient. The development attached mode may be exercised
only against an explicitly reviewed disposable Serve deployment. Do not ship it or
claim Phase 4 network acceptance until the adversarial matrix below passes
independent review.

Serve strips incoming identity headers and supplies its own for eligible tailnet
traffic. A separate untrusted local process can still contact a loopback TCP
listener and fabricate headers: loopback binding does not authenticate the proxy.
The server design must explicitly establish its trusted proxy boundary or document
and review the local-host trust assumption. Never accept a client-supplied
`Tailscale-User-Login` as proof merely because the TCP peer is loopback. Tagged
devices and node identity availability need explicit tests; missing identity fails
closed rather than being converted into a wildcard authorization.

## Implemented pairing protocol

1. An authenticated local administrator creates a random, bounded-lifetime,
   single-use pairing challenge for one server instance and workspace. Persist only
   its digest, expiry and attempt budget. Do not expose the long-lived loopback
   administrator token in the QR code or to the phone.
2. The phone contacts the configured origin with the challenge and an installation
   identifier. Rate-limit globally and per challenge; use generic failures, bounded
   request sizes and constant-time secret comparison. Verify trusted tailnet identity
   before accepting the challenge. No endpoint may enable Serve or modify a grant.
3. Show hostname, observed tailnet identity, daemon instance and workspace to the
   user. Confirmation must bind the enrollment to these exact identities; a changed
   identity invalidates the ceremony. Consume the challenge transactionally while
   issuing a random per-install credential, with hashed server storage and explicit
   revocation. Lost enrollment responses require a new ceremony, not secret replay.
4. Encrypt the credential with an Android Keystore key, atomically persist the
   ciphertext, then save a non-secret profile. Key invalidation requires re-pairing.
   Authenticated list/rename/revoke operations must never return credential values.
5. Authenticate every RPC/SSE/maintenance operation against both credential and
   paired identity/workspace policy. Revocation cancels active streams. Restore or
   identity change requires reconciliation before any mutation or native action.

The development implementation fixes challenges at five minutes, stores only their
digests, enforces bounded attempts and single use, generates random per-install
credentials, stores only credential digests on the daemon, and makes revocation close
the matching SSE stream. Rotation is revoke and re-pair. Local proxy attestation
remains an explicit deployment trust assumption: identity headers are accepted only
when the operator enables the reviewed Serve boundary on a loopback-only daemon.
Independent review and the adversarial real-Serve matrix remain required before any
release claim.

## Adversarial acceptance

Test an allowed phone and a second device outside its grant. Also test direct LAN,
raw tailnet/public IP, HTTPS redirect, alternate authority, system proxy, spoofed
Serve headers, missing identity, changed tailnet, expired/replayed/raced challenge,
stolen/revoked credential, concurrent revoke/SSE, and server restore. With Tailscale
disabled and a Funnel-published hostname, no credential-bearing workspace operation
may succeed. Keep test data disposable and never log identities, credentials,
challenge payloads or workspace content. Record failures without request bodies.

## Sources

- [Tailscale Serve and identity headers](https://tailscale.com/docs/features/tailscale-serve)
- [Tailscale Funnel exposure model](https://tailscale.com/docs/features/tailscale-funnel)

The proxy-spoofing and route-proof requirements above are threat-model deductions,
not claims that Tailscale supplies a particular Android attestation API.
