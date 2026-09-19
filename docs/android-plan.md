# Snook Android implementation plan

## Purpose and release definition

This plan covers a production-quality Snook Android application built with C#,
.NET 10 for Android, and Avalonia UI 12. The deliverables are a signed APK for
direct installation and a signed Android App Bundle for store distribution. The
application must provide the complete Snook experience on phones, tablets,
foldables, and resizable Android windows rather than a reduced companion client.

The Android application has two explicit host modes:

- **Independent**: the application owns one private SQLite workspace on the
  Android device and uses `SnookBackend` in-process. It works without a network.
- **Daemon-attached**: the application uses `DaemonBackendClient` to access one
  explicitly paired Snook daemon through Tailscale. The daemon remains bound to
  loopback and is published only inside the user's tailnet with Tailscale Serve.
  The client never opens a local workspace, silently falls back to independent
  mode, or treats cached projections as authoritative.

These modes do not synchronize with each other. Changing modes changes the
selected workspace. Moving data between them is an explicit verified backup and
restore operation, with destructive restore confirmation and ownership checks.
Peer/cloud sync, merge, and an offline mutation queue are separate future work.

An Android release is complete only when every current `IBackendClient` feature
works in both modes where the operation is meaningful, adaptive phone and tablet
layouts pass the acceptance matrix below, native Android integrations behave
correctly through process death and device restart, and the signed artifacts pass
installation, upgrade, backup, security, accessibility, and performance gates.

## Product principles

1. Preserve the existing Domain → Contracts → Persistence/Application → UI
   boundaries. Android is a composition root and native integration layer, not a
   second business-logic implementation.
2. Keep `IBackendClient` the only workspace boundary. Contract additions must be
   implemented and tested in embedded, daemon, desktop, CLI, and Android paths.
3. Treat independent and daemon-attached workspaces as separate identities. Show
   the active host and workspace on connection, settings, backup, and destructive
   screens.
4. Make Android lifecycle events ordinary. Rotation, resize, folding, background,
   process eviction, notification actions, and activity recreation must not lose
   committed data or overwrite an open draft.
5. Persist instants in UTC while preserving local civil dates, time zones, DST
   gaps/folds, and elapsed duration. A running timer is reconstructed from stored
   timestamps; it is not maintained by an in-memory tick counter.
6. Use native system surfaces where they improve the experience, while keeping
   domain behavior in shared C#. Notifications, Live Updates, widgets, shortcuts,
   sharing, permissions, secure credentials, and background scheduling belong
   behind narrow Android adapters.
7. Request the least privilege at the moment a feature needs it. Denial must leave
   the core app usable and explain the affected feature without repeated prompts.
8. Make every daemon path tailnet-only. Health, RPC, change streams, pairing, backup,
   restore, and administrative operations use the same Tailscale-protected endpoint;
   there is no public-Internet, port-forwarded, or ordinary-LAN transport fallback.

## Platform baseline and early feasibility gate

Create a short, time-boxed platform spike before feature work. Pin the exact SDK,
workload, and package versions after the spike; all NuGet versions remain in
`Directory.Packages.props`.

- Target `net10.0-android` in a new `src/Snook.Android` project and use the current
  compatible `Avalonia.Android` package. Keep `Snook.UI` platform-neutral.
- Target Android API 36 for the initial store release and set the minimum SDK only
  after running Avalonia, SQLite, Tailscale Serve/TLS, notification, and widget
  spikes on the proposed floor. The working goal is API 26 or newer, but
  compatibility evidence, not preference, sets the final minimum.
- Produce `arm64-v8a` as the required release ABI. Decide whether to include
  `armeabi-v7a` and `x86_64` only from device/support and size evidence. Emulator
  builds may remain separate from the store bundle.
- Prove a trimmed Release build, reflection-based daemon dispatch preservation,
  SQLite native loading, time-zone data, AXAML resource loading, JSON
  serialization, and notification receiver activation. Add linker descriptors or
  source-generated metadata deliberately; do not disable trimming globally to
  hide missing roots.
- Prove Debug and signed Release deployment to an emulator and a physical phone,
  and prove install/launch on a physical tablet or foldable before approving the
  architecture.
- Record package/application IDs, version-code policy, signing-key custody, minimum
  and target SDKs, ABI choices, expected download/install size, and supported
  locales as release inputs.

The spike is a go/no-go gate. If an essential native API cannot be consumed safely
from .NET for Android, implement the smallest Java/Kotlin binding or Android
library wrapper and expose it to C#; do not move the Avalonia UI or Snook domain
model to another framework.

## Proposed project and dependency shape

Add these projects or equivalent modules after the spike:

- `src/Snook.Android`: Android application, `MainActivity`, manifest/resources,
  dependency composition, deep-link routing, lifecycle integration, receivers,
  services, widget providers, and signed packaging.
- `src/Snook.Platform`: optional platform-neutral interfaces and records for
  notifications, lifecycle, secure secrets, files, sharing, haptics, scheduling,
  and adaptive-window information. It must not depend on Android.
- `tests/Snook.Android.Tests`: host-independent tests for Android coordinators,
  intent/action validation, notification projection, state restoration, profile
  security, and responsive presentation decisions.
- `tests/Snook.Android.Instrumentation`: device/emulator tests for lifecycle,
  permissions, notifications, widgets, storage picker, process death, accessibility,
  and the two backend modes.

`Snook.Android` references `Snook.UI`, `Snook.Application`, and the Android-native
adapters. Shared UI view models accept platform capabilities through interfaces;
they must not reference `Android.*`. Desktop supplies no-op or desktop-specific
implementations so the same UI remains buildable and testable.

Refactor `App` startup so desktop and Android each create a lifetime-appropriate
shell around a shared workspace session. Remove assumptions that the primary
surface is an Avalonia `Window`; use a top-level navigation host that can live in
an Android activity and in the existing desktop window. Preserve the intentionally
mixed compiled-binding policy until every inherited binding is resolved.

## Backend modes

### Independent mode

- Place the workspace, WAL/SHM files, ownership lease, receipts, and internal
  metadata in Android private internal storage. Never request broad storage access.
- Open exactly one `SqliteStore`/`SnookBackend` per application workspace session.
  Dispose it when the session ends and make activity recreation reattach to the
  session rather than opening a competing store.
- Keep SQLite schema, migrations, checksums, exact receipts, mutation log, staged
  restore, and verified backup semantics identical to desktop. Android-specific
  schema forks are prohibited.
- Use a process/session owner suited to Android lifecycle. Backgrounding an
  activity does not imply closing the backend, but process termination must leave
  the database recoverable through normal SQLite and stale-owner recovery rules.
- Exclude live SQLite files, daemon credentials, and ephemeral notification state
  from Android Auto Backup/device transfer until an atomic restore protocol is
  designed and tested. User data portability uses Snook's verified backup flow.
- Resolve device-local data paths through an injected Android path provider; do
  not reuse desktop environment-variable or command-line assumptions.

### Daemon-attached mode

The daemon remains loopback-only. The shared desktop transport now has an
implemented Tailscale Serve path with strict endpoint classification, profile v2,
stable server/workspace identity, one-time per-device pairing, reconnect handling,
and credential revocation. Its official pairing/profile/backend flow is covered
through a `.ts.net`-shaped HTTPS boundary, including RPC, SSE interruption and
reconnect, identity pinning, revocation, and proof that the attached client creates
no SQLite workspace. Android must reuse that protocol and its bounds rather than
create a mobile-only transport. Android still owns its Keystore adapter, profile
lifecycle and UI, and the real-device roaming/live-tailnet acceptance matrix. Do not
weaken the loopback bind, add a LAN listener, or copy the local recovery token.
Tailscale is the sole planned remote transport; Tailscale Funnel is prohibited.

The reference topology is:

`Snook Android → Tailscale Android VPN → tailnet HTTPS/MagicDNS → Tailscale Serve → http://127.0.0.1:<daemon-port> → Snook daemon`

1. Keep `snookd` listening on loopback exactly as it does today. Configure
   `tailscale serve --bg` on the daemon host to reverse-proxy the daemon's loopback
   port through its tailnet-only HTTPS name. Tailscale terminates HTTPS with its
   automatically provisioned certificate. Never document or automate `tailscale
   funnel`, router port forwarding, a public reverse proxy, or a `0.0.0.0` bind.
2. Require MagicDNS and HTTPS certificates in the tailnet. Match the implemented
   `DaemonEndpoint` policy: accept only a canonical
   `https://<machine>.<tailnet>.ts.net/` root on port 443, with no path, query,
   fragment, user information, IDN, raw IP, custom domain, or Tailscale Service
   alias. Disable redirects, cleartext traffic, system HTTP proxies, and
   alternate-host credential forwarding. All health, RPC, SSE and pairing traffic
   uses this one origin.
3. Publish a least-privilege Tailscale grant/ACL example that permits the intended
   Android user/device or tag to reach only the Snook Serve HTTPS endpoint. Treat
   tailnet membership alone as insufficient authorization. Acceptance must prove an
   allowed device works and a second tailnet device outside the grant is denied.
4. Preserve the implemented boundary between Tailscale identity and Snook
   authentication. Tailscale provides the private network and grant enforcement;
   Snook still requires a five-minute, single-use pairing ceremony and issues a
   random per-install credential whose host copy is only a salted verifier. An
   invitation may require the normalized `Tailscale-User-Login`, and the observed
   login is inventory metadata, but no identity header may bypass the Snook
   credential. Any stronger Android user/node binding needs a reviewed shared
   protocol revision, not a mobile-only assumption.
5. Reuse `/transport/v1/pair` and the authenticated local administration routes.
   Pair in person through the Serve URL using the current short-lived code, with a
   QR wrapper only as a future encoding of the same bounded invitation. Display the
   canonical origin, stable server ID, stable workspace ID, workspace name and
   contract before confirmation. Cancellation revokes the just-issued credential.
   Pairing cannot enable Serve, alter policy, or enroll the phone in Tailscale.
6. Store the private Snook client credential using Android Keystore-backed
   encryption. Store only non-secret Serve URL, tailnet/server/workspace identity,
   and preferences in the profile. Support list, rename, test, revoke, repair, and
   re-pair flows; never export secrets in logs, backup, clipboard, screenshots, QR
   codes after enrollment, or Tailscale configuration.
7. Reuse the existing classified `DaemonBackendClient` transport so contract
   serialization, version negotiation, server/workspace identity checks, error
   mapping, exact receipts, SSE/change semantics, and cancellation remain shared.
   Android profile migration must preserve the version-2 transport kind, canonical
   origin, protected credential reference and expected identities. Long-lived SSE must be verified
   through Tailscale Serve, including roaming between Wi-Fi and mobile data and
   direct-to-relayed path changes. Avoid a mobile-only API surface.
8. Define Tailscale-disabled/not-connected, tailnet policy denial, MagicDNS failure,
   Serve disabled, HTTPS/certificate failure, revoked Snook credential, protocol
   mismatch, and daemon outage as distinct recovery states. Every state fails closed
   and offers no LAN, public-network, raw tailnet-IP, or embedded fallback path.
9. On reconnect, authenticate, negotiate the contract, load a fresh snapshot, and
   only then resume changes. Prove snapshot/stream ordering and cursor-gap recovery.
   Drafts survive; automatic mutation replay is limited to operations with exact
   caller-owned receipts and remains an explicit user action after uncertainty.

The Android app relies on the installed Tailscale Android client and the device's
active tailnet VPN; it does not embed a Tailscale control client, request reusable
auth keys, or administer the tailnet. Detect availability by reaching the configured
Serve origin and report actionable setup guidance without trying to control another
app. VPN lockdown, always-on VPN, key expiry, device approval, node revocation, and
tailnet switching must all produce safe, intelligible failure states.

Attached mode is online-first, not offline sync. A small encrypted derivative cache
may hold the last display projection and scheduled-notification descriptors so the
app can explain an outage and cancel stale system surfaces. It is never writable as
workspace state, never presented as current after its freshness boundary, and is
discardable. No client-side `workspace.db` may exist in attached mode.

## Application lifecycle and state ownership

Introduce a `WorkspaceSession` state machine with at least: `Unconfigured`,
`Connecting`, `ReadyIndependent`, `ReadyAttached`, `StaleAttached`, `Locked`,
`Closing`, and `Fatal`. Serialize transitions and make every state observable by
the UI and native integration coordinators.

- Scope committed workspace projections to the session and draft state to the
  owning editor. Refreshes, activity recreation, and reconnect must not overwrite
  drafts. Conflict recovery remains visible and actionable.
- Save only bounded, non-secret navigation and draft-restoration metadata in
  Android instance state. Large or sensitive drafts use a private encrypted draft
  store with expiry and explicit discard. Never serialize backend objects,
  credentials, or an open SQLite handle.
- Route cold start, warm intent, notification action, widget action, shortcut, and
  deep link through one validated intent router. Reject unknown actions, oversized
  extras, unsafe URIs, stale session IDs, and identifiers not found through the
  backend.
- On process recreation, reconnect/open the selected host, reconcile committed
  state, then restore navigation and drafts. A notification action can perform only
  its named idempotent operation and must surface an uncertain result for later
  reconciliation.
- Track app foreground/background independently from activity creation. Pause
  expensive visual ticks and change-stream UI work when invisible, but preserve
  timer truth in persisted timestamps and refresh immediately on resume.
- Handle configuration change, multi-window resize, fold/unfold, font-scale change,
  locale/time-zone change, low memory, and OS process kill in automated tests.

## Adaptive Avalonia UI

Base layout decisions on current available window width and height in density-
independent pixels, not a phone/tablet device flag. Recompute while running. Use
these initial width classes, subject to visual testing: compact below 600 dp,
medium from 600 to below 840 dp, expanded from 840 to below 1200 dp, then large and
extra-large for wider desktop-style Android windows. Compact height below 480 dp
must suppress layouts that assume generous vertical space.

### Compact phone experience

- Use a single primary pane, bottom navigation for the most-used destinations, and
  a More destination for the remainder. Keep the current location and active host
  apparent without consuming a permanent desktop sidebar.
- Present editors and inspectors as full-height pages or accessible modal sheets,
  with predictive/system Back mapped to cancel/discard confirmation before leaving.
- Put Today, quick capture, active timer, and next actions within thumb reach. Use
  a floating or bottom action only when it has one unambiguous meaning.
- Replace dense desktop toolbars and hover-only affordances with labeled touch
  actions, overflow menus, swipe actions with visible alternatives, and minimum
  48 dp hit targets. Destructive gestures always require an undo or confirmation.
- Keep board lanes horizontally navigable with an explicit lane picker and
  previous/next alternatives. Do not require drag and drop; retain move menus and
  keyboard/switch-access operations.
- Use native-feeling date/time pickers where they improve touch input, while
  preserving Snook's explicit date, zone, fold, and gap validation.

### Medium and expanded tablet experience

- Use a navigation rail at medium widths and an optional expanded navigation pane
  at expanded widths. Prefer list-detail or board-detail two-pane layouts rather
  than scaling phone cards across empty space.
- Keep task lists/boards/calendar in the primary pane and a selected inspector or
  editor in a secondary pane when width and height allow. The same editor state
  must survive collapsing back to one pane.
- Allow Today to show focus/timer and upcoming/capture regions side by side.
  Calendar week/day views, board columns, history timelines, settings, habits, and
  journals should use the added width without increasing information density past
  accessible reading and touch limits.
- Support landscape, split screen, freeform/desktop windowing, fold transitions,
  hardware keyboard, mouse/trackpad, stylus hover where available, and drag-and-drop
  only as an enhancement.

### Shared mobile interaction quality

- Draw edge-to-edge with safe-area/inset handling for status/navigation bars,
  display cutouts, gesture navigation, and the on-screen keyboard. No action or
  validation message may be hidden by an inset or IME.
- Follow system light/dark mode, contrast, reduced-motion/animation preferences,
  200% font scaling, locale, 12/24-hour clock, and dynamic color where a stable
  Avalonia theme mapping is feasible. Text, icons, and accessible names must carry
  state in addition to color.
- Preserve keyboard focus containment and return, screen-reader traversal order,
  semantic headings, live-region restraint, switch-access reachability, and visible
  focus/pressed/selected/drag states.
- Use haptics sparingly for confirmed timer transitions, successful reordering, and
  invalid drop boundaries. Respect system haptic settings.
- Add skeleton/progress, empty, stale, permission-denied, and retry states for every
  data-bearing surface. Never cover an editable draft with a global loading layer.

## Full feature parity

Implement and accept each family through both backends before declaring mobile
parity:

- Today/dashboard, search, quick capture, focus session, next actions, upcoming
  items, recent work, manual time, and restored-timer recovery.
- Tasks in list and board forms; projects, sections/lanes, activities, priorities,
  deadlines, stars, tags, dependencies, references, lifecycle actions, soft delete,
  restore, batch edit, and non-drag movement.
- Concurrent timers, pause/resume/stop, manual intervals, history, corrections,
  provenance, task/activity defaults, and elapsed-duration behavior across process
  death, reboot, wall-clock change, and time-zone change.
- Calendar day/week/month/agenda, recurrence, all-day/overnight/overlap rendering,
  explicit planning, local-zone DST validation, inspection, and starting planned
  work.
- Habits, seven-day controls, bounded history, older-date correction, streaks,
  archive/delete/restore, and accessible text state.
- Journals, revision-safe editing, conflict recovery, search/navigation, delete and
  restore, plus privacy-safe notification behavior.
- Settings, connection profiles, organization maintenance, backup, restore, JSON/CSV
  export, diagnostics, about/version/contract details, and data-deletion flows.

For every mutation, preserve revision checks, exact operation receipts, stable
caller IDs, transaction boundaries, notification-after-commit, bounded work, and
uncertain-write recovery. Any contract change receives embedded/daemon parity tests
and version negotiation updates in the same change.

## Native Android experience

### Notifications and Android Live Updates

Create an Android notification coordinator fed by committed backend projections,
not UI controls. Use separate user-configurable channels for active timers,
reminders, habits, and service/connection status. Ask for `POST_NOTIFICATIONS` in
context after explaining the value; denial must not prevent timers or core data
operations inside the app.

- An active timer gets one ongoing notification with task/activity text, a system
  chronometer derived from the persisted start instant, and authenticated immutable
  `PendingIntent` actions for pause/resume/stop and opening the exact timer. Multiple
  active timers use stable per-timer IDs and a bounded group summary.
- On Android versions that support promoted ongoing Live Updates, request promotion
  only for a user-initiated active timer that meets platform criteria. Use the
  native standard/chronometer presentation and status chip; do not depend on custom
  `RemoteViews`. On older versions or when promotion is disabled/ineligible, the
  same timer remains a normal ongoing notification.
- Completion, reminder, habit, and restored-timer notices are ordinary notifications,
  never Live Updates. Journal text and private task descriptions are hidden on the
  lock screen by default; offer a privacy setting for notification detail.
- Notification actions carry only opaque IDs/nonces, validate current revision and
  session/workspace identity, and use the same application operation pipeline.
  Receipt replay is safe; changed/stale intent data fails visibly.
- Reconcile notifications after app start, backend reconnect, timer changes,
  restore, permission changes, boot, package upgrade, and time-zone change. Remove
  a surface immediately when its entity ends, is deleted, or belongs to a different
  selected workspace.

Do not keep a foreground service alive merely to increment elapsed time; Android's
chronometer and persisted instants provide that display. During the spike, determine
whether any genuinely user-visible long-running operation requires a declared
foreground-service type. Use one only when platform semantics and Play policy fit,
with a required visible notification, timeout handling, explicit stop, and tested
battery behavior. A generic `specialUse` service is not a shortcut for continuous
daemon connectivity.

### Reminders and background work

- Add explicit reminder fields to the domain/contract only if current entities
  cannot represent the desired semantics. Store the chosen local date/time, zone,
  DST interpretation, delivery policy, and stable reminder ID transactionally.
- Use AlarmManager only for user-requested precise reminders whose timing justifies
  exact delivery. Ask for exact-alarm access only if the final product truly needs
  it and provide an inexact fallback. Use WorkManager for deferrable reconciliation,
  rescheduling, cleanup, and bounded retry—not for a continuously open SSE stream.
- Reschedule from authoritative independent data after boot/time-zone/package
  events. In attached mode, schedule only from a bounded, signed/fresh derivative
  index and label behavior when the daemon has not been reachable; never invent or
  mutate workspace state offline.
- Do not promise exact delivery under Doze, OEM battery restrictions, revoked
  permissions, force-stop, or an unreachable daemon. Surface notification health
  and a test-notification action in Settings.

### Widgets, shortcuts, and system entry points

- Ship responsive home-screen widgets in small, medium, and large forms: active
  timer; Today/next actions; habits; and quick capture. Widgets show bounded data,
  clear stale/locked state, privacy-safe placeholders, and tap through to the exact
  destination. Mutation buttons use validated idempotent actions.
- Add long-press app shortcuts for quick task, start/resume timer, journal entry,
  and Today. Consider a user-pinned Quick Settings tile for the active timer only
  after proving coherent multi-timer behavior.
- Support Android Sharesheet input for text/URLs into a prefilled task or journal
  draft; never save before confirmation. Support sharing/exporting user-selected
  content through content URIs with temporary grants, not raw filesystem paths.
- Add deep/app links only with verified domains and explicit routes. Untrusted links
  may navigate to public UI but may not contain credentials or execute mutations.
- Consider Wear OS notification bridging as passive system behavior; a dedicated
  watch app is outside this release unless separately scoped and tested.

### Files, backup, restore, and export

- Keep live data in private internal storage. Use Android's Storage Access Framework
  for user-selected backup/export destinations and restore inputs. Stream through
  content resolvers into private staged files so existing verified hash, manifest,
  integrity, size, migration, and atomic-replace rules remain in force.
- Never assume a content URI is a filesystem path, seekable, stable, or trustworthy.
  Bound names and sizes, reject special/recursive selections, persist URI permission
  only when needed, and release grants that are no longer used.
- Share completed exports with a `FileProvider`/content URI and read-only temporary
  access. Redact filenames and errors shown in diagnostics.
- Restore requires foreground confirmation naming the current and incoming
  workspace, an authenticated/unlocked app state, a valid manifest/hash, sufficient
  free space, and a staged rollback path. Rebuild native notifications/widgets only
  after the replacement database commits and reopens successfully.

### Device security and privacy

- Offer optional app lock using device credential/biometric authentication. It
  protects UI, credentials, widgets, and notification details, but is not advertised
  as database encryption unless an independently reviewed encrypted SQLite design
  is implemented.
- Keep daemon client secrets behind Android Keystore and use hardware-backed keys
  when available. Handle key invalidation by requiring re-pairing, never by falling
  back to plaintext storage.
- Set release components `exported=false` unless an intentional Android entry point
  requires otherwise. Protect receivers/services/providers with explicit intent
  validation, nonces, immutable pending intents, URI grants, and signature-level
  permissions where suitable.
- Disable cleartext traffic. Restrict daemon destinations to configured Tailscale
  Serve HTTPS names, use normal certificate validation plus the paired server/tailnet
  identity, and prohibit redirects, Funnel, generic LAN hosts, raw public addresses,
  and proxy credential forwarding. Keep bounded request bodies, deadlines,
  cancellation, and redacted diagnostics. Never log credential material,
  Tailscale identity headers, journal/task content, intent payloads, database paths,
  or backup contents.
- Define screenshot/recents behavior: lock/pairing/secret screens use secure-window
  protection; ordinary workspace screenshot blocking is a user privacy option so it
  does not unnecessarily break accessibility or support workflows.
- Publish an accurate data-safety declaration, privacy policy, dependency inventory,
  SBOM, and retention/deletion description. No analytics, crash upload, advertising
  ID, contacts, location, microphone, camera, or broad file permission is added
  without a separately approved purpose and consent design.

## Connection and onboarding flows

First launch offers **Independent workspace** and **Connect to a Snook daemon** with
plain-language consequences. It does not preselect a mode that could create a new
database unintentionally.

- Independent onboarding creates the private workspace only after confirmation,
  sets local time zone/clock preferences, explains backup responsibility, and offers
  notification setup after the user reaches a feature that benefits from it.
- Attached onboarding first verifies that Tailscale is installed, connected to the
  intended tailnet, and able to reach the daemon's HTTPS Serve/MagicDNS name. It then
  scans a pairing QR code or accepts the Serve URL/pairing code manually, verifies
  tailnet/server identity and contract compatibility, names the device, stores the
  issued credential, and loads a snapshot. Camera access is optional; manual pairing
  remains complete. Snook never accepts a public, LAN, Funnel, HTTP, or raw-IP URL.
- A workspace switcher lists independent and paired daemon profiles with host,
  freshness, and identity. Switching drains in-flight work, asks about drafts and
  uncertain mutations, closes the old backend, clears its native surfaces, then
  opens the new session. It never copies data.
- Connection recovery distinguishes Tailscale disconnected, wrong tailnet, tailnet
  grant/ACL denial, MagicDNS failure, Serve disabled, daemon stopped, HTTPS trust
  failure, revoked Snook credential, incompatible contract, server restore, and
  request timeout. Trust/authentication failures require repair or re-pairing;
  reachability failures offer bounded retry and retain drafts. None offers a less
  private transport fallback.

## Delivery phases and gates

### Phase 0 — Architecture, threat model, and platform spike

Approve lifecycle ownership, native adapter boundaries, target/minimum SDK and ABIs,
linker strategy, Tailscale Serve threat model, tailnet grant example, pairing
protocol, credential storage, and adaptive navigation prototypes. Exit only with
signed Release builds running on a physical phone and tablet and with critical
technical risks demonstrated.

### Phase 1 — Android composition root and independent vertical slice

Add the Android project, shared application shell, private path provider, lifecycle
state machine, independent backend, compact/expanded navigation, task capture, task
list, one editor, timer start/stop, and verified local persistence. Prove rotation,
resize, process kill, restart, migration, and no competing store owner.

### Phase 2 — Adaptive full UI parity

Convert every existing surface to window-class-aware mobile presentation while
retaining desktop behavior. Complete all feature families, draft/conflict semantics,
touch alternatives, insets, hardware input, accessibility metadata, and phone/tablet
visual baselines. No native-system extras may mask missing core feature parity.

### Phase 3 — Native integrations

Deliver notification channels and permission education, ongoing timer notifications,
eligible Android Live Updates with fallback, notification actions, reminders,
WorkManager reconciliation, boot/time-zone handling, widgets, shortcuts, sharesheet,
haptics, secure app lock, Storage Access Framework backup/restore/export, and content
URI sharing. Complete battery and process-death tests.

### Phase 4 — Tailnet-only daemon attachment

Reuse the implemented loopback daemon, Tailscale Serve configuration contract,
MagicDNS/HTTPS grant guidance, pairing/revocation routes and profile-v2 identity
model. Add Android Keystore credentials, Android profile persistence,
snapshot/stream reconciliation, outage UX,
and actual daemon-backed notification/widget behavior. Run the entire contract suite
through the Serve origin, including mobile roaming, DERP-relayed paths, policy denial,
wrong-tailnet, Serve-down, and node-revocation cases. Ship attached mode only after
independent security review and adversarial tailnet tests prove that LAN, Funnel,
public, raw-IP, and cleartext paths cannot be selected.

### Phase 5 — Hardening and distribution

Complete performance, memory, battery, accessibility, localization, upgrade,
rollback, destructive restore, dependency, privacy, and store-policy reviews.
Automate reproducible signed APK/AAB generation, provenance/SBOM, artifact hashing,
Play internal-track delivery, and direct APK installation. Promote through internal,
closed, and production tracks with crash/ANR monitoring that is explicitly opt-in or
privacy-reviewed.

## Test and acceptance matrix

### Shared correctness

- Run existing Domain and Application suites unchanged, then add Android session and
  coordinator tests. Every `IBackendClient` method has semantic parity scenarios for
  independent and daemon-attached clients, including exact replay, conflict,
  cancellation, bounded input, delete/restore, notification cursor, and restart.
- Test migration from every supported database version and APK upgrade across every
  supported release boundary. A failed migration or restore preserves the original.
- Inject `TimeProvider` for timer, reminder, DST, notification, and cache-freshness
  tests. Cover gaps, folds, manual clock changes, time-zone changes, and reboot.

### Device/configuration coverage

- API levels: minimum, 33 notification permission boundary, 34 foreground-service
  rules, 35, 36 Live Update/large-screen behavior, and latest available preview as
  non-blocking signal.
- Windows: compact phone portrait/landscape, compact-height landscape, medium tablet,
  expanded tablet, foldable cover/inner transition, split screen, freeform window,
  display cutout, gesture and three-button navigation.
- Inputs/access: touch, hardware keyboard, mouse/trackpad, TalkBack, Switch Access,
  200% font, high contrast, dark/light, reduced motion, RTL, 12/24-hour clock.
- Lifecycle: rotate/recreate every editor, Home/return, recent-task restore, OS kill,
  low memory, force-stop, reboot, package upgrade, permission revoke, Keystore key
  invalidation, and independent/attached switching with drafts and uncertain writes.

### Native and network fault coverage

- Notification denied/granted/revoked; channels disabled; Live Update promoted,
  demoted, dismissed, and unavailable; action replay; stale IDs; locked device;
  multiple timers; reboot and restore cleanup.
- Doze, app standby, battery saver, OEM background restriction, no network, captive
  portal, Tailscale disconnected, wrong tailnet, expired/revoked Tailscale node,
  MagicDNS failure, grant denial, direct/DERP path transition, Serve restart, HTTPS
  certificate change, revoked Snook pairing, daemon stop/restart, stream overflow,
  cursor gap, server restore, slow body, timeout, and cancellation.
- Storage picker cancellation, malicious/slow provider, non-seekable URI, insufficient
  space, interrupted copy, invalid manifest/hash, export collision, app uninstall,
  reinstall, and device transfer exclusions.

### Quality budgets

Set measured budgets during Phase 0 and enforce them in CI/release reports: cold and
warm launch, first usable content, list/board/calendar scroll smoothness, input
latency, peak managed/native memory, APK/AAB size, background CPU/network, wakeups,
and 24-hour battery impact with and without an active timer. Test Release/trimming;
Debug performance is not release evidence.

### Release commands and evidence

Retain the repository's mandatory restore, full build, full test, and
`git diff --check` sequence. Add deterministic Android commands for workload restore,
unit tests, emulator/instrumentation suites, signed `dotnet publish` for APK and AAB,
bundle/APK inspection, install/upgrade/uninstall, manifest permission/component
audit, linker-warning audit, and artifact hashes. CI secrets provide signing inputs
through protected files or environment-backed secret handling and never command-line
logs.

For each candidate, archive the exact commit, SDK/workload/package locks, test device
matrix, screenshots, accessibility results, performance/battery measurements,
permission and manifest audit, SBOM, signatures, and SHA-256 hashes. A successful
build alone is not mobile release acceptance.

## Documentation work accompanying implementation

- Update `README.md` with supported Android versions, installation channels, modes,
  pairing, privacy, backup responsibility, and known platform limits.
- Update `docs/operations.md` with loopback daemon plus Tailscale Serve setup,
  MagicDNS/HTTPS prerequisites, least-privilege grants, explicit Funnel prohibition,
  pairing, revocation, upgrades, key expiry, node removal, incident recovery, and
  Android backup/restore procedures.
- Update `docs/ui-design.md` with window classes, mobile navigation, sheets/two-pane
  behavior, touch targets, insets, Back, native surfaces, and accessibility rules.
- Update `docs/progress.md` only as implementation and verification evidence lands;
  do not claim Android packaging, store readiness, native review, or assistive-
  technology validation in advance.
- Add user-facing notification/privacy and troubleshooting documentation before the
  first external test track.

## Explicit non-goals and deferred decisions

- No automatic sync or merge between the Android independent workspace and another
  workspace.
- No offline write queue for daemon-attached mode and no fallback to local SQLite.
- No daemon network exposure except tailnet-only HTTPS through Tailscale Serve. No
  Tailscale Funnel, public endpoint, router port-forward, generic reverse proxy,
  ordinary LAN listener, raw tailnet-IP profile, or non-Tailscale remote mode.
- No claim that Android Live Updates are available on every device; ordinary ongoing
  notifications are the required fallback.
- No permanent foreground service solely to keep SSE connected or a timer ticking.
- No broad storage, location, contacts, microphone, advertising, or background camera
  permission.
- No Wear OS, Android Auto, ChromeOS-specific, or iOS implementation in this scope,
  although responsive Android windows and bridged notifications must not break them.
- Database-at-rest encryption, cloud relay/push, multi-workspace sync, and managed-
  device/enterprise deployment require separate threat models and plans.

## Definition of done

The work is done when a clean device can install the signed APK, create an
independent workspace, complete every Snook workflow offline, survive lifecycle and
reboot scenarios, and perform verified backup/restore through Android system file
pickers; the same build can pair securely through an HTTPS Tailscale Serve endpoint
to a loopback-only daemon, complete the same contract workflows without creating a
client SQLite workspace, survive Tailscale roaming/outages and daemon restart without
losing drafts, and revoke its device credential. Tests must prove that no public,
LAN, Funnel, raw-IP, or cleartext daemon path is usable. Phone and tablet/foldable
layouts must be intentionally adaptive and
accessible. Timer notifications and eligible Live Updates, actions, reminders,
widgets, shortcuts, sharing, privacy controls, and app lock must reconcile with the
authoritative backend across process death. Full repository and Android device suites,
security and permission audits, signed APK/AAB inspection, install/upgrade tests,
performance/battery budgets, documentation, and release evidence must all pass with
no unresolved release-blocking findings.

## Platform references

- [Avalonia Android deployment](https://docs.avaloniaui.net/docs/deployment/android)
- [Android adaptive window size classes](https://developer.android.com/develop/adaptive-apps/guides/use-window-size-classes)
- [Android app orientation, aspect ratio, and resizability](https://developer.android.com/develop/adaptive-apps/guides/app-orientation-aspect-ratio-resizability)
- [Android notification runtime permission](https://developer.android.com/develop/ui/compose/notifications/notification-permission)
- [Android Live Update notifications](https://developer.android.com/develop/ui/views/notifications/live-update)
- [Android foreground services](https://developer.android.com/develop/background-work/services/fgs)
- [Android persistent work and WorkManager](https://developer.android.com/develop/background-work/background-tasks/persistent)
- [Android app widgets](https://developer.android.com/develop/ui/views/appwidgets/overview)
- [Android Storage Access Framework](https://developer.android.com/training/data-storage/shared/documents-files)
- [Tailscale Serve](https://tailscale.com/docs/features/tailscale-serve)
- [Tailscale Serve command](https://tailscale.com/docs/reference/tailscale-cli/serve)
- [Tailscale MagicDNS](https://tailscale.com/docs/features/magicdns)
- [Tailscale grants](https://tailscale.com/docs/features/access-control/grants)
