# Snook implementation progress


## RSS reader (2026-09-22)

- Shared RSS 2.0/Atom subscriptions, bounded manual refresh, offline plain-text
  articles, read/star state, title search, pagination and reversible subscription
  deletion are implemented across desktop, CLI, and Android independent/attached
  backend paths. Contract 1.10 adds six methods; SQLite migration 12 and JSON
  export schema 6 preserve RSS data through backup/restore/export.
- Parser, network bounds/address policy, exact offline receipt replay, deduplication,
  revision conflicts, migration checksums, backup/restart, CLI and shared-view-model
  coverage pass. Full solution verification: 291 application tests and 19 domain
  tests, zero build warnings/errors. Daemon and tailnet contract tests cover the
  RSS method surface; live saved-profile CLI and GUI acceptance fetched a public
  sample feed and verified no client SQLite creation.
- Seeded/empty desktop and minimum-size captures were inspected, including persisted
  read/star/filter/draft and cached-content checks. Screenshot profile loading now
  honors saved connection settings, matching the actual client startup behavior.
- Android Debug x64 and ARM64 APKs built with zero warnings/errors. API 36 x64 adb
  acceptance subscribed/refreshed a real feed, changed read/star state, filtered,
  and expanded cached content. A network-disabled force-stop/restart restored RSS
  navigation and preserved all five cached articles and read/star state. Evidence:
  `artifacts/rss/`; reproducible offline check: `scripts/verify-android-rss.py`.
  No physical-device test or production-signing/release-readiness claim is made.
- Scope and use: [RSS reader](rss-reader.md). Refresh is manual; no background
  polling, OPML, enclosure downloads, or full-page scraping are implemented.

Updated: 2026-09-20

## Current state

Snook is a working .NET 10 Avalonia application for local work organization and
time tracking. Its desktop and Android composition roots share one backend contract,
persist independent workspaces to SQLite, and can optionally use an authenticated
daemon. Android Phases 0–4 are implemented and pass the available automated gates;
the external and physical acceptance items below remain pending. This is not yet a
finished multi-platform release.

## Implemented surface

- Android Phases 0–4 implementation now includes explicit timed task deadlines and
  native reminders. The shared model persists a UTC instant plus chosen zone while
  the editor preserves local date/time and explicit earlier/later DST-fold choice;
  gaps fail validation. Android reconciles at most 64 reminders into inexact alarms,
  revalidates current backend state at delivery, repairs through WorkManager and
  boot/time-zone/package events, and stores only a Keystore-authenticated 24-hour
  attached derivative for clearly labeled outage notifications. Settings explains
  permission limits and posts a test notification. Optional device-credential app
  lock hides activity UI before authentication. All utility editors now use a
  bounded, workspace-bound, Android-Keystore-encrypted recovery record that retains
  exact mutation requests, canonical timed arguments, conflict revisions, and bulk
  target revisions across process death. The final manifest audit includes required
  network access without broad storage, camera, or location permissions and keeps
  cleartext transport disabled. Focused reminder, DST, deadline and editor-recovery
  tests pass; all 293 shared tests pass (274 application, 19 domain), and Android x64
  Debug plus trimmed Release build with zero warnings. Physical
  tablet/foldable acceptance remains explicitly pending and nonblocking by project
  direction; real Tailscale Serve/ACL/roaming/DERP and independent security-review
  evidence remain external acceptance work.

- Attached Android restore upload, multi-profile management, four shortcuts and four
  widget surfaces are implemented. Restore uses a credential-bound expiring stage,
  repeats identity/cursor verification at commit, blocks concurrent device dispatch,
  rebinds only the authorizing device after cross-workspace replacement, revokes
  other devices/streams, and retains the previous database. Profiles support
  list/select/test/rename/repair/revoke/forget. Native physical-device acceptance
  remains pending.

- Phase 4 now has an end-to-end development pairing and attached-session slice.
  The loopback daemon creates five-minute, attempt-bounded single-use challenges;
  observes a trusted Serve login only when the operator explicitly enables that
  boundary; persists only challenge/credential digests; binds a device credential
  to installation, login, Serve origin, daemon instance and workspace; lists and
  revokes device records; and closes that credential's SSE stream on revocation.
  `snook-cli pairing create|list|revoke` provides the authenticated loopback admin
  surface. The Android onboarding accepts only canonical HTTPS `*.ts.net`:443
  origins, reviews every bound identity, stores the credential in a distinct
  Android-Keystore AES-GCM envelope while keeping the profile non-secret, and only
  selects attached mode after a fresh authenticated snapshot. There is no attached
  SQLite file or independent fallback. Retry, forget/re-pair and explicit host
  switching preserve workspace-bound encrypted drafts while draining the old
  borrower and clearing timer surfaces. Paired task/batch/habit/journal calls,
  restart persistence, identity substitution, revocation, SSE closure and bounded
  backup/JSON/CSV download streaming pass loopback Serve-adapter integration tests.
  The later milestones above supersede this historical test count and its former
  restore/profile gaps. Actual Tailscale Serve/ACL/roaming/DERP/device acceptance and
  independent security review remain external acceptance work.

- Earlier Phase 4 foundation milestone (superseded by the end-to-end slice above):
  `DaemonBackendClient` retains HTTP-loopback-only behavior for desktop/CLI and adds
  a distinct tailnet policy accepting only HTTPS DNS origins below `.ts.net` on port
  443 with no user-info, path, query or fragment; HTTP, IPs, alternate ports and
  suffix lookalikes fail before credential use. Its default handler already disables
  redirects and system proxies. `WorkspaceSession.OpenAttachedAsync` validates the
  daemon capability before publishing `ReadyAttached`, makes initial failure
  retryable as `StaleAttached`, reuses one borrower and refuses cross-host reuse.
  Tests prove a failed/retried attached open never invokes the independent SQLite
  factory and reject an embedded backend masquerading as attached. At that milestone,
  full and Android Debug builds had zero warnings/errors and all 259 shared tests
  passed (241 application, 18 domain); later work above replaces its old UI status.

- Android cold/warm activity entry now uses one bounded fail-closed router for normal
  launch, task share and timer-notification content open. Shares require exact
  `SEND`/`text/plain`, one text extra at most 300 characters, and no URI, stream,
  ClipData, token or unknown key. Timer content requires the exact internal action,
  matching uppercase-hex 256-bit token and canonical internal route without port,
  query or fragment; the encrypted record/current backend checks remain a second
  gate. Unknown actions, malformed parcels, payload-bearing launcher entries and
  substitutions reject without navigating, saving or replacing the draft. Fourteen
  shared cases pass. The API 36 x64 Debug run in
  `/tmp/snook-android-entry-router-final-20260920/evidence` passed valid cold/warm
  sharing plus unexpected-extra, `VIEW file:` and forged-timer rejection; inspected
  screenshots retain the Tasks draft. Android and full solution builds have zero
  warnings/errors, and all 246 shared tests pass (228 application, 18 domain).
  Shortcuts, widgets, external/deep links and physical-device acceptance remain open.

- Android now restores bounded workspace navigation after process death. A versioned,
  workspace-bound private no-backup snapshot records the current destination and
  task, calendar, summary, Settings, board and journal subview selections; it excludes
  search/filter text, capture/editor content, credentials and mutation requests.
  Writes are serialized, staged and flushed after 250 ms and on backgrounding. Invalid
  state is ignored, unavailable board/journal selections fall back through current
  backend data, and a recovered task editor takes precedence by reopening Tasks.
  Three shared tests cover codec/workspace bounds, full restoration and stale-board
  fallback. The final API 36 x64 Debug run in
  `/tmp/snook-android-navigation-recovery-final-20260920/evidence` verified the file
  stayed under 2 KiB without capture text and restored Tasks across two different-
  process restarts, both with and without a recovered capture. Both PNGs were
  inspected. The final Android and full solution builds have zero warnings/errors;
  all 232 shared tests pass (214 application, 18 domain). Recent-task/OS-pressure,
  every destination, utility-editor, physical-device and accessibility acceptance
  remain pending.

- Android task editing now has bounded process-death recovery in the independent
  development host. Every unsaved task field and the original expected revision are
  checkpointed after 400 ms and on backgrounding to a separate workspace-bound,
  Android-Keystore AES-GCM file under private no-backup storage. Recovery reloads
  authoritative data and opens the normal editor without saving; a changed record
  retains the normal revision-conflict path. Cancel/Save clears the file. The fresh
  API 36 x64 Debug run in
  `/tmp/snook-android-editor-recovery6-20260920/evidence` verified bounded ciphertext
  without fixture plaintext, a different-process restore, no automatic mutation,
  Cancel cleanup and a clean second restart. Final recovery/cleared PNGs were
  inspected. Fresh desktop task-drawer interaction captures in
  `/tmp/snook-task-editor-recovery-desktop-20260920-images` passed. The Android and
  full solution builds have zero warnings/errors; all 229 shared tests pass (211
  application, 18 domain), as do Python syntax and whitespace checks. Utility-editor
  recovery, newest pre-checkpoint keystrokes, power-loss/storage/key-loss cases and
  physical-device acceptance remain pending.

- Real Android timer notifications now project committed independent-workspace
  running/paused sessions through a bounded shared projection. Settings explains the
  feature before requesting permission; low-importance ongoing notifications use the
  Android chronometer, private task/activity content, a redacted public lock-screen
  version, validated Time tracker content routing and Pause/Resume/Stop actions.
  Random opaque action tokens resolve to fixed-size Keystore-authenticated private
  records containing the revision and stable operation receipt; stale/wrong-workspace
  actions fail without mutation, and restore/foreground/backend changes reconcile
  native state. The non-exported receiver supports process-death action delivery
  without a foreground service or tick worker. A second credential-protected,
  non-exported receiver rebuilds from an existing selected workspace after completed
  boot and handles package replacement, wall-clock and time-zone changes without
  creating a clean-install database. The destructive API 36 x64 verifier
  passed education/prompt, running presentation, content open, Pause, notification
  survival, different-process Resume, actual reboot reconstruction, post-boot Stop
  and encrypted-record cleanup. Android 16 promotion eligibility is now requested
  only by the first deterministic running timer; paused timers and summaries retain
  the ordinary surface without the request. Settings conditionally exposes the
  system Live Update access handoff. The final run in
  `/tmp/snook-android-live-update-Final02/evidence` confirmed request metadata on
  running and boot-reconstructed notifications, its absence while paused, and the
  usable ordinary fallback; this emulator could not resolve the platform access
  screen and did not grant promoted treatment. The final PNGs were inspected. The
  merged manifest confirms
  both receivers are `exported=false`, the system receiver is not direct-boot-aware,
  only the four intended actions plus `RECEIVE_BOOT_COMPLETED` are declared, and the
  promoted-notification permission is present.
  The Android Debug build and zero-warning full build pass, as do all 227 shared
  tests and seeded/empty 980×640 desktop Settings captures. Actual promoted/demoted
  Live Update presentation, native time/time-zone/package-replacement acceptance,
  custom artwork, reminders, attached-mode notifications and physical-device
  acceptance remain pending.

- Compact calendar Month now replaces seven squeezed phone columns with a week
  picker, 48 dp previous/next controls and seven full-width, vertically reachable
  day cards below 600 dp; wide panes retain the complete seven-column overview.
  The selected week survives refresh and width-mode rebuilding, due-task popups and
  item inspection remain shared, and empty days are explicit. Seeded and empty
  480/720-pane verification reached both boundary weeks and the existing persisted
  due-task workflow passed. Captures in
  `/tmp/snook-calendar-month-threshold-20260920-images` were inspected. The updated
  Debug APK also passed on the API 36 x64 emulator: native hierarchy checks changed
  weeks, reached all seven day cards, and the initial/scrolled captures in
  `/tmp/snook-android-month-final/evidence` were inspected. Physical-device and
  assistive-technology acceptance remain pending. The zero-warning full and Android
  Debug builds pass, as do all 227 shared tests and tracked/untracked whitespace
  checks.

- Android clean-install host selection no longer creates the independent database
  on activity startup. The first surface presents independent and daemon-attached
  consequences without preselection; independent creation requires a resumed,
  unlocked native confirmation and persists its non-secret device-local choice only
  after the backend opens. Back cancellation and the currently unavailable daemon
  entry leave SQLite absent, and the latter explicitly fails closed without a local
  or less-private transport fallback. Existing development databases are adopted on
  upgrade. The destructive-only `--onboarding` verifier passed on the API 36 x64
  emulator through clean launch, unavailable attached entry, cancellation, confirmed
  creation, force-stop and restart; screenshots were inspected in
  `/tmp/snook-android-onboarding-final-AYNokw/evidence`. Full build and all 225 shared
  tests pass; Android Debug builds with zero warnings/errors. Secure attached
  onboarding, host switching and physical-device acceptance remain pending.

- Android confirmed restore is connected end to end: native selection and verified
  identity review, explicit replace/discard consent, resumed/unlocked-activity and
  conservative free-space gates, guarded backend handoff, serialized capture cleanup,
  old-projection retirement and fresh workspace/capture projection. The synthetic
  probe notification and real timer notifications/action records are removed before
  replacement, and the real coordinator attaches only to the reopened workspace;
  widget integration remains pending. API 36 acceptance restored a different disposable workspace,
  verified its actual SQLite identity after process restart, discarded the synthetic
  capture, retained the previous database and removed document staging. Cancellation,
  Back and rotation checks also pass. Evidence and inspected native screenshots:
  `/tmp/snook-android-restore-KpRtxd/restore-verified` and sibling `cancel`. Full build
  and 225 shared tests pass; Android Debug builds cleanly. Optional app lock,
  low-storage/provider/interrupted-copy/process-kill-during-restore coverage and
  physical-device acceptance remain pending. This supersedes the earlier read-only
  restore milestones below.

- Session restore handoff: independent sessions now return revocable backend
  borrowers. Restore closes call admission, drains existing operations, rechecks the
  confirmed workspace ID/cursor and backup identity/hash, and returns a fresh
  borrower/snapshot only after replacement reopens. Old projections cannot write to
  the replacement. Stale confirmation and pre-dispatch cancellation preserve the
  borrower; post-dispatch uncertainty retires it and keeps ownership in a fatal
  session until explicit disposal. Tests cover different-workspace replacement,
  actual old/new UI capture commands, draining, stale confirmation, cancellation
  after commit and lost responses. Full build and 225 tests pass. Native review and
  editor Back/rotation checks pass through the guard in
  `/tmp/snook-session-guard-CEwVXz`. Android now invokes this mechanism through the
  confirmed native restore flow described above.

- Calendar history initial loading now honors the injected clock, like subsequent
  refreshes. The paging test uses a controlled local-midday clock, avoiding an
  accidental extra day-column item when its old live-time fixture crossed midnight.

- Initial native read-only backup review milestone (superseded above): Android Settings opens the system document
  picker, stages the selected bundle and calls verified inspection before showing
  current/incoming workspace names and IDs, backup time and size. The dialog
  explicitly says replacement is not enabled, requires a resumed activity and
  unlocked device, and dismisses on Back/background/rotation. API 36 picker
  cancellation, valid review, Back/rotation dismissal and normal staging cleanup
  pass in `/tmp/snook-native-review-5GCLIl/native-verified`. Native review and seeded/
  empty 980×640 desktop Settings screenshots were inspected; desktop restore labeling
  and behavior remain unchanged. Full build and 218 tests pass; Android Debug builds
  cleanly. Destructive activation, workspace-bound draft/native-state handoff,
  app-lock authentication and free-space/provider/process-kill gates remain pending.

- Verified incoming-workspace inspection: contract 1.7 adds `InspectBackupAsync`
  through embedded and daemon implementations and the derived CLI catalog. It
  verifies the pair and runs the same schema/integrity preparation as restore on a
  private copy, returning workspace identity and original backup metadata without
  replacing live data or emitting a mutation. Embedded/daemon invalid-input tests,
  cancellation, source preservation and v7 migration inspection pass. Full build
  and all 218 tests pass; Android Debug builds cleanly. Saved-profile CLI inspection
  and the daemon-backed GUI connection checks pass in
  `/tmp/snook-backup-inspection-bUYClv`, including no client SQLite. Android restore
  selection, confirmation/security/free-space gates and native reconciliation are
  still pending; this provides the verified identity needed by that confirmation.

- Restore document staging foundation: `WorkspaceBackupBundle.StageAsync` accepts
  non-seekable streams, bounds the incoming copy, preflights ZIP/ZIP64 directory
  allocation, and extracts only the fixed database/original-manifest pair into an
  empty private staging directory. Tests cover non-seekable short reads, ZIP64
  metadata, traversal/duplicate/directory entries, oversized manifests, cancellation,
  and directory budgets. The bundle round trip now uses this reader and proves a
  corrupted staged database is rejected without replacing the current workspace.
  Full build and all 217 tests pass; Android Debug builds cleanly. This is container
  staging, not backup trust or completed Android restore: the native picker,
  workspace-naming confirmation, authentication/free-space gates and post-restore
  native reconciliation remain pending.

- Native JSON/CSV document export: Settings uses the same canonical private-stage,
  system-picker and read-back verification flow as backups. CSV captures its existing
  report range once from the injected clock before opening the picker. All three
  export/cancellation/cleanup checks pass on the API 36 emulator in
  `/tmp/snook-native-exports-7D22jb`; JSON schema-5 sections and the empty CSV header
  were inspected from the actual downloaded documents. Full build and all 209 tests
  pass; Android Debug builds cleanly. Android restore selection and the broader
  provider/low-storage/process-death/physical-device matrix remain pending.

- Android independent backup export: system save picker, private verified snapshot,
  exact database/manifest ZIP bundle, bounded transfer, full document read-back hash
  and staging cleanup. Canonicalizing the OS-private path fixes Android alias
  rejection without relaxing backend path safety. API 36 cancellation/export checks
  pass in `/tmp/snook-android-backup-Vdlfk1/canonical-private-stage`; the actual
  exported document restored into a fresh desktop workspace via the normal CLI
  verification path. Full build and all 208 tests pass; Android Debug builds cleanly.
  Android restore picker, interrupted staging cleanup,
  provider-failure/low-storage matrices and physical-device acceptance remain.

- Adaptive board lanes and navigation: pane-based compact widths, full-row project
  names, labeled More actions and selection-aware previous/next controls. Seeded
  stress and empty captures at 320/480/720 dp in
  `/tmp/snook-board-verified-tK3xln` were inspected. Reachability, touch-size and
  boundary checks pass, as do persisted drag/drop, edge scrolling, task moves,
  draft retention, save/conflict and Escape checks. Full build and all 207 tests
  pass. Native board touch/accessibility acceptance remains pending.

- Android editor Back confirmation: keep/discard choices, keyboard-first Back,
  confirmation cancellation and rotation retention, plus unchanged root exit.
  Fixed the earlier Android key path closing the editor before the Back callback.
  `--editor-back` passes on the API 36 emulator; native dialog and rotated draft
  captures in `artifacts/android-spike-api36/editor-back-key-routing` were inspected.
  Shared tests cover task/utility discard identity and stale confirmations. Full
  build and all 207 tests pass; Android Debug builds cleanly. Predictive gesture,
  popup-ordering, other-API and physical-device acceptance remain pending.

- Narrow calendar time-grid navigation: wider day columns, explicit day picker and
  previous/next actions, plus pinned hour labels during horizontal pan. Seeded and
  empty 480/720-width captures in `/tmp/snook-calendar-final-fqqHu0` were inspected;
  first/last-day reachability and boundary states pass alongside existing calendar
  interaction checks. Full build and all 206 tests pass. Android Debug builds cleanly;
  API 36 emulator startup/restart, rotation, background return and share/draft checks
  pass in `artifacts/android-spike-api36/adaptive-calendar-share`. Native taps reach
  Sunday with the forward action disabled; its screenshot shows the pinned hours.
  Month-grid adaptation is completed in the later milestone above; physical-device
  acceptance remains pending.

- Compact calendar toolbar/inspector and agenda: narrow range/actions stack, view
  choices wrap, and agenda names/times/actions occupy separate rows. Populated and
  empty captures in `/tmp/snook-calendar-review-h2ebRM` were inspected; toolbar
  touch-size and width checks pass at 480/720, as do existing navigation, overlap,
  inspection, planned starts and planning-validation checks. Full build and all
  206 tests pass. The later time-grid and Month milestones above complete shared
  grid adaptation; native-device acceptance remains.

- Adaptive habits: narrow panes stack creation/date controls and habit actions;
  seven-day buttons retain at least 48 dp touch targets while wrapping. Resize checks
  preserve unapplied dates. Populated/empty captures in
  `/tmp/snook-habits-review-Gs6lA7` were inspected. Persisted checks pass for creation,
  check/undo, backfill, conflict review, drafts/focus, Escape and archive/delete/
  restore. Full build and all 206 tests pass; native/assistive-device acceptance
  remains pending.

- Adaptive journals: narrow panes stack the picker, creation/settings actions and
  search/tag filters; dates/empty headings and entry actions wrap. Resize checks
  preserve unapplied search text and restore wide filter placement. Populated/empty
  captures in `/tmp/snook-journals-review-VdGxFf` were inspected, and persisted journal
  checks pass for writing, movement, filtering, mood, conflict recovery, draft/focus,
  Cancel/Escape and delete/restore. Full build and all 206 tests pass. Journal
  process-death drafts and native input/accessibility acceptance remain open.

- Adaptive History/Summary: narrow rows separate titles from date/duration/Edit
  actions and put labeled attributed/coverage totals below group bars. Desktop
  columns return after resize. Seeded and empty 480/720/980-width captures in
  `/tmp/snook-reports-final-eBJF0n` were inspected; checks verify touch-height Edit
  actions, metric labels and restored wide positions. A resize-time Grid exception
  was fixed by retaining column definitions for hidden headings. Full build and
  all 206 tests pass. Native-device/accessibility acceptance is still outstanding.

- Adaptive timer workspace: compact/medium widths show running sessions before
  activities, with one page scroll, wrapped creation actions and separate state
  labels. Expanded columns return on resize. Seeded and empty 480/720/980-width
  captures were inspected; host checks verify layout restoration and retention of
  the manual-time draft. Full build and all 206 tests pass. This is shared-layout
  evidence, not physical-device or full native timer acceptance.

- Android quick capture now checkpoints to a private Keystore-encrypted file and
  restores title/project plus pending request identity. A pre-dispatch checkpoint
  failure prevents task creation; a new regression verifies unchanged retry keeps
  its request. Clearing capture deletes its recovery copy. Full build and all 206
  tests pass. This does not restore task/utility editor drafts or complete the
  process-death acceptance matrix.

- Quick-capture recovery now has bounded, versioned, workspace-scoped metadata
  containing title, project and any pending creation request. The UI can restore
  that request without changing its exact-retry identity, and refuses to overwrite
  an existing capture/editor or restore into an unavailable project. Codec and
  committed-response-loss regressions pass; full build and all 205 tests pass.
  Android encrypted storage and lifecycle wiring are not implemented yet, so this
  does not claim process-death draft recovery on-device.

- Android now opens the independent Today workspace directly. Development probes
  are behind Tools, with a compact on-device host label. Ordinary component launches
  no longer show a share rejection. Bounded plain-text sharing has a separate
  review/discard inbox and protects existing capture/editor drafts; it never saves
  automatically. Shared build and all 203 tests pass (one daemon-watch timeout on
  the first run, full rerun green). These are Phase 1/partial UI improvements, not
  completion of Android feature parity or native integration acceptance.

- Full presentation refreshes now coalesce within each visibility period. A burst
  queues one trailing pass rather than overlapping bootstrap refresh chains; fresh
  resume is independent of an obsolete blocked read. Burst and stale-generation
  regressions pass, with the full build clean and all 196 tests green. Individual
  page/editor reads, process-death restoration and physical acceptance remain open.

- Closed a remaining refresh-time task-picker lifecycle gap: its query now receives
  the presentation token, and obsolete responses cannot publish picker options or
  continue refreshing hidden/disposed views. Four cooperative/non-cooperative
  hide/dispose cases pass; full build and all 195 tests pass. Fresh seeded/empty
  captures and the workspace/editor-retention check pass. Physical acceptance
  remains pending at the user's direction (phone available, no tablet).

- Presentation read cancellation: primary page reads now use a visibility-scoped
  token, with fresh scope on resume and quiet expected cancellation in navigation
  commands. Nine new cases verify cooperative cancellation and that task creation
  is excluded. Existing non-cooperative response tests remain green. Full build,
  editor/calendar captures and all 191 tests pass. Request coalescing and physical
  acceptance remain pending.

- Page-lifecycle stale-response guards: all primary page loaders invalidate pending
  results across visibility/disposal, and Today/History stage replacement rows.
  Eight delayed-response cases cover Tasks, Today, History, Calendar, Summary,
  Habits and journal catalog/entries; hidden responses neither publish nor continue
  read chains. Full build, editor-retention checks and all 182 tests pass.
  Transport cancellation, request coalescing and physical acceptance remain open.

- Android presentation lifecycle: activity visibility and probe selection now gate
  display ticks and backend-driven projection refresh. Resume refreshes committed
  state without replacing drafts; a generation guard rejects an earlier visibility
  period's delayed bootstrap. Three new lifecycle tests, the hidden-editor refresh
  check, full build and all 174 tests pass. In-flight page-work cancellation,
  process-death recovery, battery and physical acceptance remain pending.

- Medium-width navigation rail: 600–839 dp windows use a labeled 88 dp rail with
  scrollable 64 dp destination buttons; Today/Tasks retain stacked content.
  Boundary checks cover 599/600/839/840 dp, retained capture and Settings scroll
  reachability. Contrast was corrected during visual review. Native landscape
  captures, emulator regression, full build and all 171 tests pass. Physical
  tablet/foldable acceptance and complete adaptive feature layouts remain pending.

- Compact Tasks pass: toolbar and capture stack below 600 dp, list actions move
  beneath task text, and creation form width is bounded. Headless assertions verify
  compact placement, wide-layout restoration and retained capture text. Seeded and
  empty 980×640 captures were inspected; all 171 tests and full build pass.
  Board lanes, short-height/IME behavior and physical acceptance remain pending.

- Compact Today refinement: timers and next tasks now precede the agenda; task
  actions move below their text at compact width, and timer Stop has a visible
  label. Seeded/empty captures and retained-draft/card-order/action-row assertions
  pass. The full solution builds without warnings and all 171 tests pass.
  This remains partial adaptive UI work, not mobile or physical acceptance.

- First compact layout pass: below 600 dp, bottom navigation replaces the sidebar,
  Today panes stack and drawers fit the viewport width. Resizing preserves the
  same editor draft. Desktop seeded/empty 980×640 and compact-width captures pass;
  the x64 emulator verifies rotation, More → Habits → Today and retained capture
  text. All 171 tests and Release publishing pass. Medium navigation rail, remaining
  feature layouts, native Back and physical acceptance are still pending.

- Android development workspace host: activity views now borrow an application-
  owned backend and shared UI model, separate from the platform probe database.
  The installed x64 Release package opens the complete workspace and retains an
  unsaved capture through actual activity recreation in the same process. Native
  probes, desktop host recreation, seeded/empty captures and all 171 tests pass.
  The portrait layout is visibly cramped and not accepted; adaptive UI, native
  Back, process-death drafts and physical-device acceptance remain pending.

- Independent session ownership foundation: `WorkspaceSession` serializes startup
  and close, shares one backend across repeated opens, validates committed state
  before readiness, isolates observer failures, and cleans up cancelled startup.
  Four new disposable-workspace tests cover concurrency, retry, cancellation and
  lease release. Full build has zero warnings/errors and all 170 tests pass.
  Android activity wiring and session-owned UI projections remain pending;
  attached/locked lifecycle states are reserved only, not implemented.

- Shared Android UI foundation: extracted the complete workspace AXAML and pointer/
  keyboard handlers into `WorkspaceView`, leaving `MainWindow` as the desktop host.
  Ancestor bindings no longer require a `Window`; detachment stops view-local work
  without disposing the session. A headless recreation check preserves an editor
  draft through view replacement and backend refresh, then verifies Escape does
  not save it. The full combined 980×640 seeded interaction pass and separate empty
  captures pass; keyboard fallback after focus loss was fixed during verification.
  All 166 tests and clean x64 Android Release packaging pass. Android activity-owned
  workspace session wiring, compact/expanded layouts and physical acceptance are
  still pending; this is a foundation, not mobile feature parity.

- Fixed the Android-discovered PID-reuse ownership defect with exclusive OS file
  locking instead of PID-based stale-marker deletion. Filesystem locking is checked
  before opening the owner marker; disabled locking fails closed. New tests cover
  concurrent contenders, live legacy-owner exclusion, symlinks, cancellation,
  disabled locking and recovery after killing a real daemon. All 166 host tests
  pass. Clean x64/ARM64 Android Release publishes pass; the x64 emulator recovered
  the untouched failed workspace, retained both identity fingerprints through
  force-stop/rotation and a verified reboot. Saved-profile CLI and daemon-backed
  GUI checks also passed without a client-side database. Physical acceptance and
  the remaining Android implementation remain pending.

- Android SAF feasibility: native save/open pickers now export a 32 KiB synthetic
  fixture and verify its exact hash through bounded private staging without using
  content URIs as paths or requesting broad storage permission. API 36 x64 Release
  checks cover picker cancellation, round trip and oversized-input rejection.
  Eight host tests cover the shared non-seekable stream-copy primitive; the full
  157-test suite and x64/ARM64 Release publishing pass. This is not verified workspace
  backup/restore integration. Physical-device acceptance stays pending by user
  direction; no tablet is available. See [native evidence](android-native-spike.md).

- Android notification Phase 0: an explicitly triggered synthetic notification
  probe now requests permission contextually, avoids repeat prompts after denial,
  and posts an ordinary low-importance ongoing notification with a system
  chronometer. API 36 x64 Release checks verify denial leaves local probes usable,
  recovery through Android settings, visible advancing elapsed time, return to the
  probe and removal. ARM64 Release publishing and all 149 repository tests pass.
  Live Update promotion/fallback and durable production timer integration remain
  unimplemented; see [native spike evidence](android-native-spike.md).

- Android native Phase 0: Keystore AES-GCM interop now has a bounded encryption
  primitive and a private synthetic-secret probe. Release emulator checks verify
  tamper/purpose rejection, removed-key failure without read-time regeneration,
  and the same secret through process restart, package update and an observed
  reboot. A non-exported receiver is activated by Android in trimmed Release;
  immutable sender-substitution, nonce replay and one-shot replay checks pass,
  and Android's broadcast records confirm external delivery denial. ARM64 Release
  publishing and the full 149-test repository suite pass. This is native feasibility,
  not production credential storage, app lock, notifications or attached-mode
  acceptance; see [native spike evidence](android-native-spike.md).

- Android Phase 0: added a separate Avalonia 12 Android probe with activity-view
  factory startup, private no-backup storage, explicit local SQLite/JSON/time-zone
  checks, and Release trimming enabled. Android workload installation and package
  restore succeeded. Debug APK and partial-trim Release APK/AAB packaging succeeded
  with a development signer; manifest inspection confirms API 26/36 and disabled
  backup/cleartext. The API 36 x64 emulator now runs both Debug and partial-trim
  Release: SQLite native loading, competing-owner rejection, mutation/exact receipt
  replay after edit/reopen, bootstrap JSON and DST probes pass. A repeatable ADB/UI
  harness passes Release cold start, force-stop/relaunch and rotation, retaining
  XML/PNG/JSON evidence; probe screenshots were inspected in portrait and landscape.
  The strengthened probe retains its live store lease: force-stop/relaunch and an
  actual emulator reboot both recover with the same persisted workspace/receipt
  identity fingerprint. Final rotation checks assert actual orientation and restore
  the original device preference. The updated arm64 Release publish also passes;
  runtime coverage is still API 36 x64 only.
  Debug APKs embed assemblies for installation without IDE fast-deployment files.
  Full desktop build and all 149 tests passed again. Physical-device and wider
  lifecycle/native-feature verification remain pending. The
  [spike evidence ledger](android-spike.md) records outstanding gates and the
  [security draft](android-security.md) records the unresolved Funnel/route and
  trusted-proxy identity boundaries. Android product functionality and release
  acceptance remain unimplemented.

- Saved version-1 client profiles now let both the GUI and CLI select embedded or
  authenticated daemon mode without repeating connection flags. Profiles retain
  only host/data-directory/endpoint/token-file settings, use private atomic writes,
  and never serialize the token. Resolution is flags, environment, saved profile,
  then the embedded default; malformed/unsafe profiles fail closed, with explicit
  `--no-profile` recovery for the CLI. The desktop has visible startup correction,
  saved next-launch connection settings, actual endpoint/status display, an outage
  warning and manual retry without overwriting an open draft. The CLI `doctor`
  reports the resolved discovered endpoint. Desktop launcher actions expose daemon
  startup and connection settings. Real disposable daemon checks passed saved-profile
  CLI use plus GUI connection, task capture and timer controls without creating a
  client SQLite database. Full build has zero warnings/errors and all 149 tests pass
  (131 application, 18 domain), including connection-profile security/precedence,
  pending-window disposal and non-pumping UI synchronization-context regression
  coverage. Seeded and empty 980×640 captures were inspected. A controlled actual
  daemon stop/restart showed the stale-data warning, rejected refresh while offline,
  reconnected at the same endpoint and retained the typed draft; both 980×640 frames
  were inspected. Live credential/endpoint switching and broader update/lifecycle
  retry coverage remain open. No RPM was rebuilt;
  release 3 predates this connection-profile and later restore work. See the current
  [production-readiness summary](production-readiness.md) and the detailed
  [daemon plan](daemon-plan.md).

- Restored running timers now become explicit recovery items with open intervals
  frozen at the verified backup boundary. Last known adds no time; Stop now
  credits the gap explicitly; Continue starts a new interval now without the gap.
  Repeated backup/restore of unresolved items preserves the boundary, and old
  exact receipts cannot reopen current recovery state. Both preparation and
  resolution keep before/after provenance. Closed paused/stopped sessions remain
  unchanged, clock rollback cannot add negative time, and Continue honors the
  foreground concurrency preference and rejects hidden task/activity references.
  Dashboard cards show identity, recorded duration, the exact last-known boundary
  and the credit policy; unchanged cards preserve focus across refresh.
  Full build has zero warnings/errors; all 137 tests pass (119 application,
  18 domain). Shared foreground/background scenarios pass in embedded and actual
  daemon modes; additional deterministic tests cover restart, clock rollback,
  closed-session preservation, concurrency policy and archived/deleted activity
  rejection. Fresh 980×640 seeded, empty and restored screenshots were inspected;
  pointer-driven recovery checks persisted all three decisions with provenance.
  See [daemon-plan.md](daemon-plan.md#restored-timer-reconciliation-pass) for
  evidence paths and remaining acceptance. No package was rebuilt or installed;
  release 3 still predates these restore changes.

- Restore now requires and verifies the matching format-1 manifest, staged byte
  count and SHA-256 before SQLite validation/upgrade. Inputs reject links and
  live/recovery sidecars, with 16 GiB backup and 16 KiB manifest bounds. Moved
  valid pairs and older checked schema upgrades remain supported. Native connection
  leases exclude reads during replacement; disposal drains connections before
  releasing ownership. Restore invalidations use the ordered commit queue with
  captured metadata, and cancellation after commit does not fail a saved result.
  Missing active databases with restore recovery files fail startup rather than
  creating an empty workspace. Full build has zero warnings/errors; all 125 tests
  pass (107 application, 18 domain), and `git diff --check` passes. Embedded,
  daemon and CLI rejection/success scenarios, controlled connection/ownership
  tests and cancellation/observer cases passed with disposable profiles.
  Crash-consistent activation/recovery, active-timer reconciliation and broader
  daemon/GUI acceptance remain tracked in [daemon-plan.md](daemon-plan.md).
  The release-3 RPM predates these new restore changes. No UI layout changed.

- JSON export schema 5 adds persisted settings, a snapshot cursor, task links and
  dependency edges. Dedicated export reads preserve links under deleted tasks,
  stored link/tag/session tombstones, and deleted-session correction history.
  The versioned [format reference](json-export.md) documents data representation,
  numeric enums, compatibility, privacy and exclusions. The SQLite schema and
  backend contract remain 10 and 1.6. Embedded/daemon/CLI scenarios verify all
  supported feature families, relationship metadata, backup/restore and restart.
  Concurrent atomic task/tag updates retain matching titles, tags and cursors
  in each export. Full build has zero warnings/errors; all 119 tests pass
  (101 application, 18 domain), with clean whitespace and shell syntax checks.
  Release 3 was rebuilt from these sources with the format reference; extracted
  payload checks passed for create replay, schema-5 export/no-overwrite, CLI watch,
  ownership/fail-closed behavior and shutdown. Its transient per-user service
  passed readiness, protection settings, restart, stop and lease reopening.
  No package or normal service was installed/enabled. The post-publish full build
  and all 119 tests passed again. Exact artifact/evidence paths are recorded in
  [daemon-plan.md](daemon-plan.md).
  Restore verification, GUI connection/recovery, transport and native lifecycle
  acceptance remain open. No UI layout changed in this pass.

- Backup and JSON/CSV export no longer overwrite selected files. Shared embedded/
  daemon validation requires an absolute unused destination and protects live
  workspace/sidecar/lease/recovery and credential/discovery paths, including
  aliases. Backup refuses existing manifests and SQLite companions. Files are
  staged privately, verified/hashed, flushed and published without replacement;
  Unix artifacts are mode 0600. Backup now runs SQLite integrity/FK checks before
  publication. Exports hold the shared writer gate across their data queries.
  Embedded/daemon and CLI tests verify preservation of existing bytes, reserved
  paths, links, output hashes/privacy and repeated-call failures. Additional tests
  cover alias-opened workspaces, contention, pre-cancellation and integrity failure.
  Verification: full solution build with zero warnings/errors, all 115 tests pass
  (97 application, 18 domain), and `git diff --check` passes. Tests used disposable
  profiles with process/socket permissions; no real workspace or installed package
  was touched. No UI layout changed in this pass.
  Restore manifest verification, broader adversarial snapshot
  checks, power-loss/Windows acceptance and the broader daemon plan remain open.
  See [daemon-plan.md](daemon-plan.md) for the precise scope and limitations.

- Contract 1.6 exposes caller-owned operation requests for the 13 remaining
  convenience create/add methods, using shared embedded/daemon exact receipts.
  The CLI catalog exposes the optional requests; unchanged retries with a stable
  request return the original result, while changed arguments are rejected.
  GUI organization/task/calendar creation, manual time and tag/link/dependency
  additions retain their request and captured arguments while unchanged. Derived
  instants, time zones and activity defaults are frozen for retry. Successful
  writes clear attempts before refresh; late responses preserve newer drafts.
  Cancel is not an undo, and pending attempts are in memory, not a durable outbox.
  Verification: full build with zero warnings/errors, all 110 tests pass
  (92 application, 18 domain), and `git diff --check` passes. Embedded/daemon/CLI
  receipt tests and lost-response GUI model tests passed. Seeded 980×640 persisted
  editor/workspace checks and empty screenshots passed and were inspected.
  Broader update/lifecycle retries, saved GUI profiles/startup recovery and actual
  daemon-backed GUI checks remain open. The earlier RPM is contract 1.5, not a
  packaged verification of these new changes. See [daemon-plan.md](daemon-plan.md)
  for evidence paths and remaining production acceptance work.

- Fedora packaging now includes the GUI (`snook`), CLI (`snook-cli`) and daemon
  (`snookd`) as separate self-contained payloads, an opt-in per-user systemd unit,
  and operations/CLI documentation. The existing GUI launcher is preserved;
  installation does not start a service or open a workspace. Native runtime
  dependencies are explicit and bundled private libraries do not advertise
  global RPM capabilities. The build uses fresh directories, honors numeric
  version overrides and installs only its just-built artifact when requested.
  CLI help/examples use the installed command name. `watch` now emits one complete
  compact JSON object per physical line, with a live daemon regression check.
  Verification: built and inspected `snook-0.1.0-2.fc44.x86_64.rpm`; extracted
  payload checks passed for help, discovery, CLI read/write/watch, ownership and
  missing-token failures, shutdown, token permissions and embedded reopening.
  A transient user service using that package's unit settings passed readiness,
  effective protection settings, persisted mutation across restart, redacted
  journal, stop and lease reopening. No package or normal service was installed
  or enabled. Full solution build has zero warnings/errors; all 90 tests pass
  (72 application, 18 domain), and shell syntax / `git diff --check` pass. Actual
  native install/upgrade/uninstall, saved GUI profiles/outage UX, broader UI retries,
  reconnect ordering, daemon-backed GUI flows and Windows services remain open;
  see [daemon-plan.md](daemon-plan.md) for exact artifact/evidence paths.

- Schema migration 10 adds durable exact-result receipts for older organization,
  task, timer, calendar, settings and batch mutations. The transaction wrapper
  binds command arguments to the operation ID, returns the original serialized
  result on retry, and saves no-op receipts as well. Changed arguments are rejected;
  legacy receipts without verifiable original results fail explicitly. Existing
  habit/journal formats remain supported. Task-default activity resolution occurs
  inside the timer transaction after receipt lookup, preserving replay even after
  defaults change. Operation requests now validate IDs and revision applicability.
  Staged restore upgrades v9 sources without mutating them, and applied migration
  checksums from sequence 7 onward are checked independently. Database backups
  retain retry history; domain JSON/CSV exports keep their existing formats.
  Verification: clean full rebuild with zero warnings/errors and all 90 tests
  passing (72 application, 18 domain), including embedded/daemon exact replay after
  later edits/deletion, restart, backup/restore, changed argument rejection, no-op
  and create receipts, v9 migration, earlier checksum corruption, and rollback if
  receipt persistence fails. `git diff --check` passes. Tests used disposable
  profiles with local process/socket access. Caller operation IDs for remaining
  convenience creates, GUI request retention, maintenance path safety, service
  packaging and broader daemon acceptance remain tracked in the daemon plan.

- Shared commit notifications now come from the transaction's persisted mutation
  log across all entity mutation families. Receipt replay and rollback are silent;
  cursor, operation/aggregate IDs, revision, kind and millisecond UTC timestamp
  match the committed row. Delivery preserves commit order outside the write gate,
  tolerates reentrant handlers, and isolates observer exceptions from both mutation
  results and other observers. Older ad hoc publishers and duplicate habit/journal
  publishers were removed. Restore remains an explicit workspace snapshot reset.
  Verification: clean full rebuild with zero warnings/errors, all 86 tests pass
  (68 application, 18 domain), and `git diff --check` passes. Shared embedded/daemon
  tests compare notifications against SQLite through organization/tasks/tags,
  timers, settings, calendar and bulk operations, concurrent writes and retries.
  Additional tests cover restart replay, receipt-insertion rollback, sub-millisecond
  clock inputs, committed-row visibility and throwing/reentrant observers.
  Tests used disposable profiles with local process/socket access. Exact historical
  receipt results and request binding, restore/stream reconciliation, service
  packaging and remaining GUI connection work are still in the daemon plan.

- Daemon transport and client hardening is underway against the full specification;
  [daemon-plan.md](daemon-plan.md) tracks acceptance work and remaining gaps.
  The daemon now tracks and drains request handlers, serializes RPC dispatch,
  validates malformed/null arguments, sanitizes internal errors, bounds request
  concurrency and SSE queues, and uses one writer per change stream with
  heartbeats and write deadlines. Tokens are created privately, validated at
  startup, and kept out of readiness/error output. A private atomic endpoint
  descriptor enables custom-port discovery from the selected data directory.
  GUI launch flags and shared GUI/CLI connection resolution support simultaneous
  daemon clients; invalid host/port settings fail explicitly. Clients validate
  loopback endpoints and contract compatibility, bypass proxies/redirects, report
  useful connection errors, and dispose asynchronously. README/CLI/operations
  docs include connection, retry, discovery and token-rotation procedures.
  Verification: full solution build with zero warnings/errors, all 83 tests pass
  (65 application, 18 domain), and `git diff --check` passes. New transport tests
  cover malformed calls, private credentials/discovery, unsafe endpoints,
  incompatible contracts, missing-token fail-closed behavior, concurrent ordered
  changes, CLI doctor using discovery, SIGTERM and workspace lease reacquisition.
  Desktop and daemon `--help` were executed without opening a GUI/workspace.
  Tests required local process/socket access outside the sandbox and used
  disposable profiles. This is not completion of daemon production acceptance:
  legacy exact-receipt/notification semantics, broader adversarial/parity checks,
  per-user packaging, persistent GUI profiles and visible connection recovery,
  and Windows platform/service verification remain tracked work.

- Journaling is implemented as a sidebar screen with multiple journals, plain-text
  entries, separate journal-only tags, optional 1–7 mood ratings, title/body search,
  exact tag filtering, pagination, entry movement, and soft-delete/restore.
  The writing drawer preserves drafts across refreshes and conflicts, offers
  latest-saved comparison, contains keyboard focus, and supports Cancel/Escape
  with focus return. Deleted entries remain readable. Journal deletion preserves
  entries and their individual deleted states.
  Contract 1.5 provides matching embedded and authenticated daemon behavior.
  CLI helpers `journals` and `journal-entries`, plus `call` methods for every read
  and write, expose the full feature. Schema migration 9 adds independent journal
  tables; transactional content/tag writes and exact receipts survive restart,
  backup, and restore. JSON export schema 4 includes all journal data. Supported
  older backups are upgraded in staging without changing their sources.
  Limits: 200 journals including deleted records, 20,000 characters per entry,
  20 tags per entry, mood omitted or 1–7, and 1–100 entries per query page.
  Verification: full solution build with zero warnings/errors; all 71 tests pass
  (53 application, 18 domain); clean `git diff --check`. Embedded and daemon
  contract/CLI tests cover CRUD, moves, independent tags, validation, pagination,
  conflicts, request replay, notifications, migration, restart, export, and restore.
  Persisted journal UI checks pass at 1280×820 and 980×640, including create/rename,
  entry writing/movement, mood/tag clearing, filtering, draft preservation,
  stale-save review, focus, Cancel/Escape, and delete/restore. Seeded and empty
  page/editor PNGs and Today/Settings sidebar smoke captures were inspected under
  `artifacts/journals/{seeded,narrow,empty,empty-narrow,checks,checks-narrow}`.
  Tests used disposable profiles; VSTest/daemon checks required local sockets
  outside the sandbox. Native human and assistive-technology review remain
  separate release work.

- Daily habit tracking is implemented across the GUI and shared backend contract
  1.4, with a read-only `habits [HabitQuery JSON]` CLI helper and generic contract
  calls available to automation. The Habits page offers create/edit, direct daily
  check/undo, current streaks, a 30-day history, older-date review, and archive,
  soft-delete, and restore. Fixed creation time zones preserve civil days across
  DST and daemon host settings. Corrections cover the last 366 days; the initial
  catalog cap is 500 habits including deleted records. Weekday schedules, numeric
  targets, reminders, and automatic timer-based completion remain outside scope.
  The implementation plan is in [habits-plan.md](habits-plan.md).
  Schema migration 8 adds separate tables; exact request-bound receipts survive
  subsequent edits and restart. Backup, staged v7 restore, JSON export schema 3,
  and authenticated daemon dispatch preserve habits and their history. New
  committed notifications use the actual mutation cursor and timestamp; replay
  emits no duplicate notification. Drawer conflicts retain the draft and offer
  saved-value comparison before retry. Check-in controls retain keyboard focus,
  and sidebar navigation scrolls when needed at the minimum window size.
  Verification: full solution build with zero warnings/errors; all 60 tests pass
  (50 application, 10 domain); `git diff --check` is clean. Tests cover embedded
  and daemon behavior, revision conflicts, bounded reads/corrections, lifecycle,
  durable replay, migration, backup/export, staged restore and rejection of newer
  schemas, CLI reads, DST, midnight, and streaks longer than the read window.
  Persisted headless habit interaction checks pass at 1280×820 and 980×640,
  covering create, check/undo, backfill, draft retention, stale-save review, focus,
  Escape, archive/delete/restore, and history preservation. Seeded/empty Habits
  and editor renders plus Today/Settings sidebar smoke captures are under
  `artifacts/habits/{seeded,narrow,empty,empty-narrow}` and were visually inspected.
  VSTest required local socket access outside the sandbox. Native human and
  assistive-technology review remain separate release work.

- The headless CLI now has a dedicated reviewable mass-edit workflow. `task-batch
  plan` resolves explicit IDs or intersecting text, hierarchy, state, priority,
  favorite, and due-date filters into a deterministic preview with exact revisions;
  broad selection requires explicit `all: true`. `task-batch apply` sends that
  unchanged plan through the atomic backend operation for up to 500 tasks. Saved
  operation IDs make response-uncertain retries exact, while stale targets roll the
  entire batch back. Structured JSON output includes the operation and updated
  tasks, and the machine-readable `api` catalog advertises the workflow. Embedded
  integration coverage verifies planning, conflict rollback, selective mutation,
  tags, due dates, favorites, untouched tasks, and receipt replay; authenticated
  daemon coverage exercises the same plan/apply path. Verification: the full
  solution builds with zero warnings or errors, all 50 tests pass, and
  `git diff --check` is clean.
- Tasks supports checkbox selection in List and Board, Select all in view/Ctrl+A,
  Clear/Escape, and a bulk drawer with explicit fields to apply. Priority, due date,
  favorite, completion, project, default activity, tags, title, description, and
  archive state update atomically for up to 500 tasks. Stable selection survives
  refresh and excludes hidden tasks. Conflicts retain the draft and provide a
  latest-revision review; Cancel discards the draft. Completion retains prerequisite
  rules and permits selected prerequisites to complete in the same transaction.
  Contract 1.3 supports the same batch in embedded and authenticated daemon modes;
  durable receipts replay exact results after subsequent edits or restart.
- Tasks and projects expose star toggles and editor controls; boards do not.
  The Starred scope includes starred tasks and tasks in starred projects. Date-only
  due dates reuse the existing independent field, with a dedicated schema-7
  migration for deadline/favorite indexes. Event Day/Week/Month displays clickable
  due counts for open, unarchived tasks; due-list entries open task details and
  never create schedule blocks. Month event lists remain inside their cells.
  Verification: zero-warning full solution build, all 49 tests passing, and clean
  `git diff --check`. Shared embedded/daemon tests cover atomic rollback, selective
  fields, nullable clears, tags, moves, lifecycle changes, dependency completion,
  bounds, favorites, migration preservation, and restart-safe replay. Persisted
  headless checks cover bulk editing/conflicts/recovery/cancel/focus, selection,
  favorites and due popups at 1280×820 and 980×640. Existing workspace creation,
  drag/drop, task/utility editor, timer, and calendar checks pass at both sizes.
  Seeded and empty images were rendered and inspected under
  `artifacts/task-batch/{seeded,narrow,empty,empty-narrow}`; regression captures are
  in `regression` and `regression-narrow`. VSTest requires loopback socket access
  outside the sandbox. Native human and assistive-technology review remain separate.

- Sidebar navigation replaces all seven font glyphs with original vector icons:
  sun, checklist, stopwatch, calendar, history clock, bar chart, and gear. Shared
  24-pixel slots, rounded strokes, and subtle fills keep detail and weight consistent;
  icons inherit the navigation label's state colors without taking focus or clicks.
  Verification: full solution build with zero warnings/errors, all 47 tests passing,
  and clean `git diff --check`. All seven selected states were rendered and their
  sidebars visually inspected with seeded and empty profiles at 1280×820 and
  980×640 (28 captures in `artifacts/ui-navigation-icons`). VSTest required local
  socket access outside the sandbox. This pass covers the navigation artwork.
- Calendar has Event and History modes. Event retains its existing arrangements;
  History has Week and adaptive Flex, proportional recorded intervals through now,
  gaps for pauses, per-day tracked totals, and gray future task plans clipped at now.
  Flex uses a 156-pixel day-width target and keeps 2–21 days visible. Open intervals
  advance locally; backend timer changes rebuild the view. Overnight splits,
  short records, overlaps, 23/25-hour days, keyboard inspection, and paginated
  history retrieval have dedicated coverage. Calendar events remain in Event mode.
  Verification: full solution build has zero warnings/errors; all 47 tests pass,
  including 14 calendar tests and a 205-session pagination case. Persisted Event
  planning/navigation and History resize, mode memory, keyboard inspection, and
  pause/resume/stop checks pass at 1280×820 and 980×640. Seeded and empty renders
  were inspected in `artifacts/calendar-bimodal/{seeded,narrow,empty,empty-narrow}`.
  `git diff --check` passes. This is headless development verification; native
  human interaction and assistive-technology review remain separate release work.
- Today separates Quick capture and On the horizon with a 14-pixel gap.
  Sidebar icons remain aligned within compact navigation rows.
  Today and Tasks capture pickers group projects under disabled board headings
  and retain board labels on selected choices. Full build and all 33 tests pass;
  persisted capture, board management, drag/drop, and task drawer checks pass.
  Seeded and empty screenshots were inspected at normal and 980×640 sizes.
- Settings hides deleted activities and boards by default with separate Show deleted
  checkboxes. Deleted rows use tinted backgrounds, accent borders, and struck-through
  names alongside their Deleted labels. Tracker suppresses blank description tooltips.
  Full build and all 33 tests passed; persisted headless checks cover hide/show,
  restore, styling, and optional tooltip content at normal and minimum sizes.
  Seeded and empty-profile captures were inspected, including 980×640 layouts.
- Task-sidebar consistency: Tags and visible amber Archive/red Delete actions are
  above Save task, with the immediately committed Move action below. The current
  board/project path updates after moving, and Cancel preserves that move while
  discarding unsaved fields. Move choices have non-selectable board headings;
  prerequisites have board/project headings and contextual labels.
- Task boards use a horizontally scrolling button row with separate arrows.
  Contextual board and project-lane menus expose rename, soft delete, and saved
  earlier/later ordering. Project lanes honor stored order, and board deletion
  hides child lanes until restore. Persisted interaction checks cover menus,
  ordering, recovery, overflow navigation, grouped pickers, and move/cancel behavior.
  Seeded/empty layouts were rendered and inspected at 1280×820 and 980×640;
  interaction checks passed at both sizes, including duplicate project/task names
  across boards and capture selection after rename. Full build and all 33 tests
  passed. Evidence is in `artifacts/ui-task-actions*`.
- Quick task capture accepts Enter on Dashboard and Tasks, using the same guarded
  create command as the button. Tasks now renders board selection independently
  of project lanes: creating a board selects it, shows an empty-board prompt,
  and renders its first project as soon as it is added. All boards remains
  available; selecting a board filters both List and Board views.
  Verified with persisted keyboard/creation/drag/editor checks at 1280×820 and
  980×640, inspected seeded and empty captures, a clean full build, and 33 passing
  domain/application tests. Captures are in `artifacts/ui-capture-board-fix*`.
- Desktop modernization after an intensive rendered/code review: Settings categories
  and readable catalogs replace page-wide maintenance expanders; Tasks has compact
  rows, completion controls, inline board actions, and a filter popup; Tracker has
  toolbar creation/manual-time actions and a route to its activity library; History
  uses a ledger and correction drawer; Summary has selected groupings and proportional
  time bars. Dashboard composition and Calendar's time grid are preserved, with
  calendar creation/editing moved into focused drawers. The written review and plan
  are in `docs/ui-modernization.md`.
- Shared utility editors use compiled bindings, isolated record/picker drafts,
  fixed Save/Cancel areas, scrolling forms, keyboard focus containment/return, and
  actionable revision-conflict feedback. Project/activity tags remain explicit
  immediate actions inside the editor. Notes-only history corrections preserve
  precise interval timestamps; unsaved timer preferences survive refresh and can
  be reset to their saved value.

- Refined light palette: warm neutral surfaces, dark slate text, and deep teal actions; shared Fluent resources align dropdowns, text entry, menus, and interaction states with the workspace. The light variant is explicit rather than mixing system-dark controls with light page surfaces.

- Board/editor redesign pass: shared task drawer for board and list, preserved drafts across backend refreshes, revision-safe saves, visible conflict feedback, Tab containment and Escape cancellation. The board now supports edge scrolling while dragging, project jumps, compact capture controls, and card lift feedback. Settings maintenance is grouped into disclosures, with wrapping project/activity rows. Search labels and visibility match the current workspace.

- Calendar/Today redesign pass: date navigation, duration-based day/week schedules, overlap columns, all-day rows, overnight clipping, current-time indication, selectable month entries and a keyboard-accessible inspector. Users explicitly choose a task/calendar/time when planning, and can start a planned task from its inspector. Today puts four next actions directly beneath the current focus session; upcoming items are chronological and past completed time blocks are excluded. Live picker options preserve selections across timer refreshes.

- Workspace redesign: Grouper-inspired grouped activity launchers opposite running/paused/background session cards; independent tracker pane scrolling; a persistent sidebar focus clock; collapsed maintenance forms; horizontal project lanes with draggable cards, floating previews, highlighted drop targets, and Escape cancellation. Explicit move actions remain available. Today includes upcoming plans beside current work. Data operations live in Settings, and operation status stays visible below the page. The requested redesign is implemented and audited; see `docs/ui-design.md` for direction and evidence.

- Workspace hierarchy: boards, projects, activities, activity groups, and tasks.
- Task lifecycle: edit (including descriptions), complete/reopen, archive/restore, soft-delete/restore, search, filters, sorting, starring, deadlines, tags, links, dependencies, and task details.
- Time tracking: start, pause, resume, stop, manual entries, corrections with provenance, foreground/background lanes, concurrency settings, and restart recovery.
- Calendar: schedule blocks plus calendar events with descriptions, locations, colors, all-day support, bounded daily/weekly recurrence, recurring occurrence exceptions, visibility-aware projections, and inline event move/resize/edit controls.
- Calendar desktop workflow: event and planned-block creation now use an explicit visible-calendar selector and preserve that selection across refreshes; Day, Week, and Month render distinct date-grid layouts while Agenda remains the editable chronological view.
- Today/review: priority and active work, recent and upcoming work, history search with pagination, and summaries by day/task/project/activity/activity group/tag/lane.
- Data operations: migrations, integrity-checked backups, safe restore, JSON export, and CSV worklog export.
- Hosting: embedded SQLite desktop mode, authenticated loopback daemon mode with RPC and SSE changes, and a complete JSON CLI surface generated from the shared backend contract. The CLI supports both host modes, agent-safe named JSON arguments, operation/revision retries, and all workspace, tracking, calendar, recovery, export, backup, and restore methods; see `docs/cli.md`.
- Operations: README guidance, backup/recovery/hosting documentation, and a hardened Linux systemd service definition.
- Desktop finishing: live pickers exclude deleted records; Tasks can reveal and restore deleted tasks; Settings exposes soft-delete/restore for boards, projects, activities, groups, and calendars; deleted calendar events have a restore surface; Today now has both a fast 30-minute manual-time shortcut and an explicit task/activity, UTC start/end, and notes form.
- Ownership recovery: a dead owner PID no longer strands a workspace after an interrupted host, while a live owner still blocks a second host before writes.
- Daemon operations: readiness is emitted only after the loopback listener binds; SIGTERM (including systemd stop) now performs graceful shutdown and releases the workspace lease.
- Capture and tracking workflows: Tasks now creates boards, projects, and tasks in place; Time Tracker is a dedicated sidebar workspace for standalone activity creation, editing, lifecycle management, manual entries, and timer controls; Today can select and switch activities while keeping paused foreground sessions resumable.

## Verification completed

- Modernization: full solution build with zero warnings/errors and all 33 tests
  passing (28 application, 5 domain). VSTest required loopback access outside the
  filesystem sandbox. Seeded and empty captures at 1280×820 and 980×640 were
  rendered and visually reviewed, including catalogs, sparse states, drawers,
  bottom-of-page reachability, and Calendar's Agenda. Artifacts are in
  `artifacts/ui-modernized`, `ui-modernized-narrow`, `ui-modernized-empty`, and
  `ui-modernized-empty-narrow`.
- Combined headless checks passed at standard and minimum sizes: board creation,
  dragging/edge scrolling, task draft and stale-save handling, timers, calendar
  navigation/planning, manual time, activity creation, History validation/provenance
  and exact timestamp preservation, Settings categories, board/activity/calendar
  editing, focus containment/return, and cancellation. Further minimum-size checks
  cover timer-preference drafts/reset, immediate tags without draft loss, and
  calendar soft-delete/restore through the contextual menu. Interaction images are
  in `artifacts/ui-modernized-interactions` and `ui-modernized-interactions-narrow`.
  These are development checks; native human and assistive-technology review remain
  separate release work.

- Dashboard alignment and scrollbar refinement: the lower Today cards now share the upper row's column proportions. Scrollbars use muted gray-green thumbs, neutral tracks, and darker hover/pressed feedback. Verified with a clean solution build, all 33 tests, and persisted board/drawer/timer/calendar interaction checks. Seeded and empty dashboard captures were inspected at 1280×820 and 980×640, alongside tracker and board scrollbars (`/tmp/snook-alignment-images`).

- Final redesign audit: clean solution build, all 30 tests passing, and combined board/editor/tracker/calendar headless interaction checks passing. Daily totals advance with running sessions and refresh across midnight; domain tests cover short/long DST days. Cancel/reopen tests confirm drafts are discarded. Standard, minimum-size, empty, and stress profiles were rendered and inspected. This completes the requested development redesign, not the release/accessibility matrix.

- Board/editor pass: headless checks verify offscreen project moves in a seven-project fixture, project jumping, task draft survival, SQLite saves, stale revision rejection without draft loss, and keyboard focus containment. Narrow task drawers, Settings maintenance, and History correction forms were rendered and inspected.

- Calendar/Today pass: the headless interaction runner verifies day/week/month navigation, keyboard schedule inspection, overlap bounds, planned-task starts, invalid interval rejection, explicit planning persisted to SQLite, and picker selection preservation. All standard screenshot sections were generated, plus stress-calendar, empty, and minimum-size profiles.

- Redesign pass (2026-09-12): the full solution builds with zero warnings/errors and all 27 existing tests pass. The screenshot runner additionally verifies pointer-driven task moves against SQLite, target feedback cleanup, Escape cancellation, and timer pause/resume/stop/activity-start controls. Seeded, empty, and 980×640 captures were rendered and inspected. These checks cover the implemented tracker/board work, not completion of the full redesign or a release accessibility audit.

- Full solution build has passed with zero warnings and zero errors.
- The application suite has 25 passing tests and the domain suite has 5 passing tests, covering timer lifecycle and ownership, standalone activity switching, stale-lease recovery, migrations, recurrence including DST gap/fold behavior and exceptions, calendar event editing and visibility, task details, settings, corrections, backups/restores, ordering, and local-day boundaries.
- CLI integration checks cover generated full-contract discovery, embedded named-argument task mutation plus idempotent retry, and authenticated daemon-mode mutation without opening SQLite. The command reference documents agent workflows, mutation concurrency, calendar/time tracking, maintenance, and transport configuration.
- The daemon-backed contract test covers authenticated access, capability reporting, board creation, and committed change notification. Interrupted SSE responses are treated as reconnectable shutdown/transport events.
- Daemon RPC request bodies and argument counts are bounded before JSON dispatch; the integration test verifies oversized authenticated calls fail closed.
- Task detail durations are presented as readable elapsed labels, and primary desktop controls expose non-visual automation names.
- The redesigned UI has been regenerated through the headless screenshot harness across Today, Tasks, Calendar (including the date-grid modes), History, Summary, and Settings; the harness now builds explicitly and executes the output DLL for reproducibility.
- Desktop visual iteration uses a disposable, lightly seeded screenshot profile. Dropdown labels, task metadata bindings, section/view selection, wrapping board cards, and expandable task/history/calendar editors have been reviewed through regenerated screenshots. The harness also captures lower Today/Settings content and expanded editors; this is not a release accessibility audit.
- Release build and tests are green after the desktop finishing pass; the Release app is started against `artifacts/ui-screenshot-data` for human review.

## Known remaining release scope

- Android has a development independent host and partial adaptive UI, not a completed
  Android product. Full mobile parity, utility-editor process-death recovery, native
  integrations, Tailscale attachment and production distribution remain incomplete.
- Peer/cloud synchronization is not implemented; the daemon is a local authenticated transport, not a sync service.
- Windows service packaging is not implemented.
- The accessibility, packaging, and release-readiness matrix still needs a dedicated pass.
- The UI has had build and screenshot review; human interaction and accessibility review remain necessary.

## Useful commands

Build:

```bash
dotnet build Snook.slnx --no-restore -p:MSBuildEnableWorkloadResolver=false -m:1 -nr:false -v:minimal
```

Test:

```bash
dotnet test Snook.slnx --no-build -p:MSBuildEnableWorkloadResolver=false -m:1 -nr:false -v:minimal
```

Launch the desktop app in embedded mode using a disposable data directory:

```bash
SNOOK_DATA_DIR=/tmp/snook-ui-review dotnet run --project src/Snook.Desktop/Snook.Desktop.csproj --no-build
```
