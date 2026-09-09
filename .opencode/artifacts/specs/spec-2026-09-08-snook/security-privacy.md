# Security and Privacy

## Privacy promise

Default Snook is local, accountless, telemetry-free, and offline-capable. It
does not imply that data “never leaves the machine” after a user explicitly
enables remote daemon access, peer sync, export, links, or backup destinations.
UI and documentation must state the active data path accurately.

## Protected assets

- Task titles/descriptions, notes, calendar details, tracked activity, tags,
  links/file paths, and behavioral summaries.
- SQLite DB, backups, exports, and diagnostic files.
- Device identity private keys, pairing tokens, local IPC credentials, and
  revocation state.
- Integrity of timer boundaries, task state, migrations, and sync operations.
- Availability of the local store and daemon.
- Metadata such as device name, workspace existence, activity times, and peer
  addresses.

## Threat model

| Threat | Examples | Baseline control |
|---|---|---|
| Other local user/process | Opens socket/DB/config, steals token | Private paths, OS ACL/permissions, store lease, peer identity |
| Network peer | Legacy-style unauthenticated pull/push | No listener by default, TLS, explicit pairing, key-bound device ID |
| Replay/stale client | Repeats stop/update or old sync op | Operation IDs, receipts, revision preconditions, replay window |
| Malformed input | Huge recurrence/archive/protocol payload | Typed validation, size/range/depth limits, bounded expansion |
| Supply chain | Compromised package/native SQLite | Lock/pin dependencies, hashes/signatures, SBOM, scanning |
| Data loss/corruption | Power loss, disk full, bad migration | Short transactions, WAL/FULL, online backup, integrity gates |
| Secret/content leakage | Logs, crash dumps, notifications | Redaction, minimal messages, protected storage, explicit bundles |
| Lost device | DB/credentials copied from storage | App-private storage; optional evaluated at-rest encryption; revocation |
| Untrusted daemon admin | Remote daemon can read plaintext | Document trust boundary; E2E field encryption is not implied |

The local OS account and a configured daemon host are trusted with plaintext in
the baseline. Peer transport encryption does not protect data from either
endpoint. If zero-knowledge hosting becomes a goal, it requires a separate key
and query architecture.

## Secure defaults

- Embedded profile opens no listener.
- Daemon local control listens only on named pipe/Unix socket.
- Remote daemon and sync listeners are disabled until explicit setup.
- mDNS/discovery is off unless selected and publishes no stable device ID or
  hostname beyond what the approved design requires.
- No automatic update request or telemetry request occurs by default.
- Web dashboards are not part of the baseline daemon. Any future browser UI has
  full authentication, CSRF/session protections, and separate review.
- Diagnostic logging defaults to information-level operational metadata with
  content fields absent, not merely masked after interpolation.

## Identity, pairing, and transport

### Local IPC

- Windows named pipe ACL permits the intended user/service identities only.
- Linux UDS lives under a private runtime directory with owner-only mode and
  validates socket ownership before connecting.
- Per-user daemon is preferred. A system service must authorize workspace/user
  mapping rather than trusting a claimed user ID.
- Loopback fallback uses a 256-bit random token, restrictive token-file access,
  rotation, and origin-independent API authentication.

### Remote access and peer sync

- Pairing requires an action at both trusted endpoints (short-lived code, QR, or
  public-key fingerprint confirmation).
- Each device has a key pair generated and stored by the platform secret adapter.
- Device ID is derived from/bound to the public key, never accepted as a string
  assertion.
- TLS 1.3 where platform support allows, with mutual device authentication or a
  comparably reviewed authenticated protocol.
- Pairing tokens expire, are single-use, rate-limited, and never logged.
- Revoked devices cannot reconnect; key rotation and lost-device workflow are
  defined before sync release.
- Requests have message/decompression/batch/rate/time limits and explicit
  protocol versions.

## Secrets by platform

- Android: Android Keystore-backed key material. Backup/restore behavior is
  tested because restored ciphertext may not have its original key.
- Windows: DPAPI/Windows credential facilities through a reviewed adapter.
- Linux desktop: Secret Service/libsecret/KWallet-compatible adapter where
  available; daemon secrets may use systemd credentials or owner-only files.
- Development `user-secrets` is not a production mechanism.
- Workspace settings and sync payloads never contain private keys/tokens.

## At-rest encryption

`Microsoft.Data.Sqlite` does not encrypt SQLite by itself. Options such as
SQLCipher introduce native packaging, key recovery, backup, Android ABI, and
licensing risks. Therefore:

1. v1 MUST use OS-private app data and strict file permissions.
2. UI MUST NOT label the DB “encrypted” unless an encryption-capable provider is
   active and verified.
3. At-rest encryption is a capability behind a spike/review and includes key
   loss/recovery UX, migration, performance, all target RIDs/ABIs, and license
   approval.
4. Export/backup encryption is separately specifiable and may be delivered
   earlier using an authenticated archive format and user-held passphrase.

Full-disk/device encryption remains valuable but is outside app control.

## Database hardening

- Never load arbitrary SQLite extensions.
- Use parameterized SQL and fixed migration resources.
- Set `trusted_schema=OFF` where compatible and validate source DB structure
  before querying dynamic legacy fields.
- Import opens source read-only/immutable when possible, copies to controlled
  storage if required, and never attaches it to the live DB.
- Daemon data path is not user-selectable to a network filesystem without an
  explicit unsupported warning/block.
- Backups and restores validate hash, manifest, schema, FK check, and quick check.

## Content and filesystem safety

- File links are opened only after user action using platform shell APIs; their
  scheme/type is displayed and dangerous schemes are rejected.
- Export archives reject absolute paths, `..`, symlinks, device files,
  oversized entries, compression bombs, and duplicate manifest paths.
- Recurrence strings have syntax, range, iteration, and timeout limits.
- Rich text/Markdown, if added, renders with HTML/script disabled by default.
- Clipboard and notifications avoid notes/task text on locked screens according
  to user privacy settings.

## Logging and diagnostics

Allowed by default:

- Event ID, app/schema/contract version, host mode, aggregate type (not title),
  operation ID, result class, duration, row counts, queue depth, and redacted
  exception category.

Forbidden by default:

- Titles, descriptions, notes, tag names, URIs/paths, SQL values, sync payloads,
  tokens, private keys, full DB paths/usernames, or raw protocol messages.

Diagnostic bundles are built locally, previewable, explicitly saved/shared by
the user, and contain no database unless separately selected with a warning.

## Security verification gates

- Architecture threat-model review before daemon remote access or sync coding.
- Static analysis, dependency vulnerability scan, SBOM, and license report on CI.
- Protocol parser fuzz/property tests and bounded-resource tests.
- IPC authorization tests with a second OS user where CI/lab supports it.
- TLS pairing/revocation/replay tests and hostile-peer assessment before network
  features leave preview.
- Export/archive path-abuse tests.
- Secret-not-in-log automated tests.
- Signed artifact and update-channel verification before auto-update is offered.

## Data rights

The user can locate their active store, create/verify backups, export documented
data, delete/purge data subject to clear
sync retention, revoke devices, and run indefinitely without an account. There
is no hidden server-side copy in the default product.
