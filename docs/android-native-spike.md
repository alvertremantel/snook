# Android native feasibility evidence

This is part of Phase 0 in [android-plan.md](android-plan.md). It records both the
isolated native probes and the later real independent-workspace timer-notification
slice. Android pairing and a Keystore-backed per-install credential store now have
a development implementation; actual Serve-backed notification acceptance
and physical-device security review remain unimplemented. The existing
[spike ledger](android-spike.md) tracks the other exit criteria.

## Keystore encryption

`KeystoreProtector` uses Android's `AndroidKeyStore` provider with AES-256-GCM,
provider-generated 12-byte nonces and 128-bit authentication tags. A versioned
envelope contains only its version, nonce, ciphertext and tag. Plaintext inputs are
bounded to 1–4096 bytes; envelope size/version, alias and purpose are validated
before native work. Associated data binds the envelope to its key alias and purpose.
Reading a missing key fails rather than generating a replacement. There is no
plaintext fallback. The key material stays with the Android provider.

`KeystoreSpike` creates one random 32-byte synthetic secret and writes only its
61-byte encrypted envelope under private no-backup storage through a staged file.
Subsequent runs must decrypt that existing envelope. It verifies:

- Successful encryption/decryption and distinct nonces on repeated encryption.
- Authentication failure after ciphertext/tag modification or purpose substitution.
- Failure after removing an isolated throwaway key, without recreating it on read.
- The same high-entropy secret fingerprint across process restart, package update,
  and a separately observed emulator reboot.

Managed plaintext buffers used by the probe are cleared after use. Only a SHA-256
fingerprint of the random fixture appears on the test screen or in evidence; no
credential, plaintext fixture or key material is logged. The removed-key test
deletes only `org.snook.spike.removed-key-test.v1`, which it created for that test.
It never removes the persistent `org.snook.spike.secret.v1` key.

These are cryptographic binding/interop checks. Hardware backing, StrongBox,
biometric/device-credential authorization, lock-screen transitions, real biometric
key invalidation, key rotation, pairing recovery, crash-safe profile/secret updates,
and OEM backup/device-transfer behavior remain unverified. The primitive currently
creates a key on encryption if absent; production enrollment orchestration must
never call that path as recovery from a failed secret read. App lock and enrollment
policy must be implemented and reviewed before real credentials use this primitive.
The SQLite workspace itself is **not encrypted** by this probe.

## Receiver activation and immutable actions

Android activates the manifest-declared `ProbeReceiver` through an explicit,
immutable, one-shot `PendingIntent` in a partial-trim/AOT Release build. Its target
is `org.snook.spike.ProbeReceiver`, with `exported=false`. A bounded, random,
process-scoped nonce authorizes only completion of this synthetic probe; it cannot
invoke backend mutations or navigate the workspace.

The probe deliberately supplies a replacement nonce in the sender's fill-in intent.
The original nonce still reaches the receiver, proving immutability for this path.
An invalid nonce and a consumed nonce are rejected, and re-sending the one-shot
native token raises Android's cancellation exception.

An external `adb shell am broadcast --user 0` attempt was also inspected in
`dumpsys activity broadcasts`. Android marked it `SKIPPED` with a permission-denial
reason identifying the receiver as not exported. The generic broadcast command's
`result=0` alone was not treated as evidence. The exact record is retained in
`artifacts/android-spike-api36/keystore-receiver-retry/external-receiver-denial.txt`.
No new application permission was added by these probes.

This proves receiver activation and token behavior while the test process is active.
By itself it does not prove notification permission/channel behavior, cold-process
notification actions, durable nonce/receipt reconciliation, widgets, reminders, or
Live Updates. The real timer extension below now covers the first three for the
independent API 36 development host; the remaining integrations still require
further native acceptance.

## Executed evidence

On 2026-09-19, API 36 Google APIs x86_64 revision 7 with emulator 37.1.11 and ANGLE
rendering passed the native probes in partial-trim/AOT Release with development
signing. ARM64 Release publishing also passed; there is no physical ARM64 execution
evidence. The full repository build passed with zero warnings/errors and all 149
tests passed. The mandatory whitespace check passed.

Evidence directories under `artifacts/android-spike-api36`:

- `keystore-receiver-release`: initial run was obstructed by an emulator **System UI**
  ANR before the probe began. This is not recorded as a passing run. After selecting
  Wait on that system dialog, `keystore-receiver-retry` passed.
- `keystore-immutable-action`: sender-substitution, process restart and rotation
  passed; the protected-secret fingerprint matched the prior package's report.
- `keystore-reboot`: initial readiness observation raced with reboot and failed
  launcher resolution. No success is claimed for this directory.
- `keystore-reboot-verified`: the harness observed a different kernel boot ID and
  matched both protected-secret and workspace fingerprints against the pre-reboot
  report. The same reboot was observed; it was not repeated after the failed poll.
- `native-final`: the finalized harness passed with the changed-boot and baseline
  fields recorded, and exact result equality across rotation. Portrait and landscape
  PNGs were inspected at 720×1280 and 1280×720 (density 240); the complete probe
  result remains visible. The disposable emulator was stopped after verification.

The harness now accepts `--after-boot-id` and `--baseline-report`. The former waits
up to three minutes for a different completed boot, tolerating transient ADB
disconnects without issuing another reboot. The latter requires persisted identities
to match the bounded prior JSON report. Rotation requires the exact same probe result
at actual rotation 1 after starting at rotation 0. Original rotation preferences
are restored, and failures/timeouts capture diagnostic XML and screenshots.

Example after recording a successful baseline (only use a disposable device):

```bash
snook_previous_boot=$(adb -s emulator-5554 shell cat /proc/sys/kernel/random/boot_id | tr -d '\r')
adb -s emulator-5554 reboot
python3 scripts/verify-android-spike.py --serial emulator-5554 \
  --after-boot-id "$snook_previous_boot" \
  --baseline-report artifacts/android-spike-api36/keystore-immutable-action/report.json \
  --output artifacts/android-spike-new-reboot
```

Keystore/receiver-only APK SHA-256 values (before the notification extension):

```text
x64 Release   3cf796f213a8051291243f799541ead36add89d78c05f2350f7211216b261bb9
arm64 Release 54af54532b685af4f38bc6c4530d0e830ebdc01c9568491fb73af4a1c0a1d2f7
```

## Primary references

- [Android Keystore](https://developer.android.com/privacy-and-security/keystore)
- [Android cryptography guidance](https://developer.android.com/privacy-and-security/cryptography)
- [PendingIntent security](https://developer.android.com/privacy-and-security/risks/pending-intent)
- [PendingIntent API](https://developer.android.com/reference/android/app/PendingIntent)
- [Notification runtime permission](https://developer.android.com/develop/ui/compose/notifications/notification-permission)
- [Live Update requirements](https://developer.android.com/develop/ui/compose/notifications/live-update)
- [Android document access](https://developer.android.com/training/data-storage/shared/documents-files)
- [Avalonia storage provider](https://docs.avaloniaui.net/docs/services/storage/storage-provider)

## Ordinary notification extension

The separate **Show synthetic timer notification** button requests notification
permission only after an explicit tap. A private preference records the request;
denial or dismissal does not trigger further prompts, including after process
restart. The screen explains recovery through Android app settings. Permission
denial does not affect the local SQLite/Keystore/receiver probes. Activity destruction
completes an outstanding permission wait without retaining a destroyed activity.

`NotificationSpike` creates a low-importance channel and checks both application
and channel notification settings before posting. The synthetic notification uses
Android's chronometer, private lock-screen visibility, an immutable activity intent,
and ongoing/only-alert-once flags. There is no foreground service, periodic worker,
app tick loop or real domain timer. A separate button removes this notification ID
only. Repeated Show starts a new synthetic elapsed-time display.

Live Update promotion is deliberately not requested: ordinary ongoing presentation
is the baseline feasibility test, not proof of promotion or a tested promotion
fallback. Promotion eligibility, user demotion, notification actions tied to durable
backend mutations and persisted timer reconstruction were later addressed by the
real extension below. Clock changes, OEM behavior, notification shade accessibility
and physical-device testing remain open.

API 36 x64 partial-trim Release evidence lives in
`artifacts/android-spike-api36/notification-probe`. The contextual permission dialog
was denied; a subsequent Show displayed the denial explanation without prompting.
`notification-denied-local` passed the existing cold-start, force-stop/relaunch and
rotation harness with both persisted identities matching `native-final`.
An initial emulator System UI ANR was dismissed before these checks; it is not an
app pass. No app data was cleared.

After enabling notifications through the actual Android app-settings switch,
Show posted successfully. Inspected `shade.png` and `shade-later.png` show the
chronometer advancing from 00:13 to 00:53 while the shade was open.
`notification-dump.txt` confirms channel importance 2, `ONGOING_EVENT`,
`ONLY_ALERT_ONCE`, private visibility and `android.showChronometer=true`.
Tapping the notification returned to the probe; Remove then left no active spike
notification record (`content-intent.xml`, `removed.xml`, `removed-dump.txt`).
UI Automator could not obtain an idle shade hierarchy while the clock updated;
`shade-stale-app.xml` is explicitly stale, not shade evidence. Screenshots and the
system notification record provide the presentation evidence instead.

The updated x64 Release build and ARM64 Release publish passed. Full desktop build,
all 149 repository tests and `git diff --check` passed. Only x64 ran on a device;
signing remains the development signer. Updated notification-probe APK hashes:

```text
x64 Release   32bdcd45578db1a16df1342e8fbbf1bf34ac29527a0286e001a77ec07c317861
arm64 Release f8097a01d433d96ea997b5bc8844545d1a01e1405d8ae97393f40dfcbd2b06c6
```

## Real active-timer notification extension

The independent Android host now has a separate real coordinator; the synthetic
Tools probe above remains only a feasibility diagnostic. A shared, host-independent
projection reads one committed bootstrap snapshot, selects only running/paused
sessions, orders them deterministically and limits the native surface to 16 items.
Task/activity text is Unicode-aware and bounded, and elapsed duration is clamped.

Settings presents an explanation before requesting `POST_NOTIFICATIONS`. Once
allowed, `active-timers-v1` posts low-importance, ongoing/only-alert-once private
notifications. Running timers use Android's chronometer from the committed displayed
duration; paused timers show a static duration. The public lock-screen version is
redacted to timer state. Content opens Time tracker. Pause/Resume and Stop use
immutable pending intents to `TimerActionReceiver`, which the merged manifest marks
`enabled=true` and `exported=false`.

The intent holds only a random uppercase 256-bit token. A corresponding fixed-size
record under private no-backup storage binds workspace/session, expected revision,
action, stable operation ID and stable client ID. AES-256-GCM through Android
Keystore authenticates each record with token-specific associated data. Records
expire after seven days, are capped at 128 and are removed when their surface is no
longer current. On delivery, the app opens/reuses the selected workspace, validates
the record against a fresh snapshot, dispatches the shared revision-checked backend
operation, and reconciles. This preserves exact-receipt identity across process
death and rejects stale or wrong-workspace actions. Restore detaches and clears the
old workspace's native state before dispatch, then attaches to the reopened
workspace; a pre-dispatch failure reattaches to the unchanged one.

On 2026-09-20, the destructive-only `--timer-notification` scenario passed on the
API 36 x86_64 emulator. The final evidence is in
`/tmp/snook-android-timer-boot-Final03/evidence`. It verified the Settings education
and native permission prompt, a real committed Admin timer with system chronometer,
private content plus redacted public content, immutable Pause/Stop actions, content
navigation to Time tracker, Pause, notification survival after `am kill`, and Resume
in a different cold-started process. It then recorded the kernel boot ID, performed
an actual emulator reboot, observed a different completed boot, required the
committed running timer and fresh Pause/Stop actions to reappear, and stopped the
timer through that reconstructed surface. All three encrypted 119-byte action
records were removed. The post-boot collapsed, expanded and removed screenshots
were visually inspected. The action receiver's `exported=false` value was also
checked in the merged Android manifest. A generic ADB shell explicit-broadcast
result was not treated as denial evidence because the shell identity may bypass
ordinary application export rules.

Reboot reconstruction uses `TimerSystemEventReceiver`, not a foreground service or
worker. It is `exported=false`, `directBootAware=false`, requires
`RECEIVE_BOOT_COMPLETED`, and accepts only completed boot, this-package replacement,
wall-clock change and time-zone change. It refuses an unselected/missing independent
workspace. With a live application it reuses the process owner; otherwise it opens
the existing shared backend just long enough to read committed state and release the
lease after posting. Locked boot cannot read the credential-protected database or
Keystore action records and is intentionally not registered. The merged manifest
was inspected for the exact permission, flags and four-action filter. Only completed
boot has native event-delivery acceptance so far; the other three paths compile and
share the same bounded coordinator but remain to be exercised as real system events.

On Android 16 and newer the first deterministic running timer requests optional
promoted-ongoing treatment. The manifest declares
`POST_PROMOTED_NOTIFICATIONS`; the notification stays ongoing, standard-style,
chronometer-based, non-colorized and not a group summary, so it remains the ordinary
fallback when promotion is unavailable. Paused timers and summaries do not request
promotion. Settings exposes a **Review Live Update access** action only on Android
16+, checks `CanPostPromotedNotifications`, and otherwise opens the app-specific
system destination when it resolves.

The final API 36 x86_64 run is in
`/tmp/snook-android-live-update-Final02/evidence`. It required the true
`android.requestPromotedOngoing` extra on both the initial and boot-reconstructed
running surface, required the extra to be absent while paused, then repeated the
full cold-process action and cleanup sequence. Android retained the ordinary
ongoing notification and did not add the promoted flag. The image also could not
resolve its promotion-access settings activity; Snook displayed the bounded
ordinary-fallback explanation. This proves request and fallback behavior on this
image, not a promoted system surface or user demotion flow.

This is production-shaped timer behavior in the development package, not Android
release completion. Actual Live Update promotion/demotion, native wall-clock/time-
zone/package-replacement event acceptance, custom monochrome artwork, attached-device
acceptance, additional API/OEM coverage and physical phone/tablet acceptance remain
pending. Timer display uses no foreground service, periodic tick worker or
application tick loop. The later product implementation added inexact timed-task
alarms, bounded WorkManager reconciliation and authoritative delivery checks.

## Storage Access Framework extension

**Export synthetic file** and **Verify synthetic file** use Avalonia's Android
storage provider and the native DocumentsUI save/open pickers. The fixture is
32,768 deterministic, non-secret bytes, not SQLite or a Snook backup. The app
accepts only a single `content:` file URI, bounds URI length, does not convert URI
or display name to a local path, and never requests a directory tree or broad
storage permission. It does not bookmark or persist the selection.

The shared `BoundedStreamTransfer` reads without querying length or seeking. It
handles short reads, enforces the caller's byte limit before writing excess bytes,
and leaves stream ownership with its caller. The spike limits transfers to 65,536
bytes and supplies a 30-second cancellation token to copying/hashing. This is
cooperative cancellation, not a guarantee that a hostile provider's open/read
implementation will obey a deadline. Import uses a generated private no-backup
staging filename, checks exact length/SHA-256, and deletes the stage in `finally`.
Failed exports may leave a partial user-selected document; the UI says so instead
of silently deleting an external provider's document.

Evidence under `artifacts/android-spike-api36/documents`:

- Native save picker cancellation and reopening passed (`save-cancel.xml`).
- Export and selected-file reopen passed exact SHA-256 and length validation
  (`exported.xml`, `verified.xml`, visually inspected `verified.png`).
- A 20,486,718-byte development APK copied as an explicitly synthetic `.bin`
  input was rejected (`oversize-rejected.xml`); cancelling the next Open picker
  returned to enabled controls (`open-cancel.xml`).
- `uri-grants.txt` shows an activity-owned temporary grant with `persisted=0x0`.
  No persistent access was taken. This is not an assertion that the provider grants
  read-only access; the native selected-document grant included read/write access.
- The first Save-picker UI dump returned a null root despite exit code zero.
  `save-picker-stale.xml` is stale app XML and is not picker evidence; the subsequent
  successful dump and screenshot are `save-picker.xml` and `save-picker.png`.
  The lifecycle harness now requires the dump's success marker before reading XML.

Eight host tests cover short non-seekable reads, zero/exact byte limits, oversize
rejection without excess writes, cancellation and invalid limits. The full desktop
build and all 157 tests pass. Partial-trim Release publishes for x64 and ARM64 pass;
only x64 was run on the API 36 emulator. These APKs still use development signing:

```text
x64 Release   1cbb90187f8c1178699b85b7c044db7b5da896646f7c1642f939233665560e22
arm64 Release b1179ac1abe22b56951970e1e862360e04ba865d2fe2070e8fef69e7e4afe97f
```

The later product implementation added verified backup/restore, JSON/CSV export and
read-only `FileProvider` sharing. Hostile/cloud providers, grant revocation,
interrupted-copy and orphan-stage recovery, storage exhaustion, picker recreation,
minimum API and physical devices remain pending. The user explicitly left physical-device
acceptance pending (phone available, no tablet); continued implementation does not
approve that release gate.

### Follow-up ownership failure — reproduced, then fixed

The strengthened lifecycle harness was run after the document checks as
`artifacts/android-spike-api36/document-regression`. It **failed** at SQLite
initialization; this directory is not a lifecycle pass. Read-only inspection of
the disposable emulator with temporarily rooted ADB established:

- The preserved legacy `.owner` marker records PID 3849, written before reboot.
- Current `/proc/3849/status` identifies `binder:1684_9`, a thread of another app
  (`Tgid: 1684`, different UID), not a Snook workspace owner.
- `SqliteStore.TryRemoveStaleLease` checks PID existence only and does not distinguish
  process/thread reuse or boot identity. A reused live ID prevents stale recovery.
- The emulator has ample free storage. Neither the workspace nor owner marker was
  cleared or edited; the failure and diagnostic evidence were retained as
  `failure.xml/png`, `stale-owner.txt`, `reused-pid-status.txt` and `boot-id.txt`.

The failure established requirements for the independent workspace session. It
must continue to reject a genuine live owner before database writes, recover after
crash/reboot/PID reuse, and avoid concurrent stale-removal races. Merely deleting
this marker or adding a timeout would hide the defect, not satisfy the gate.
Earlier lifecycle/reboot passes remain historical samples, not evidence that this
case works. The document round trip and 157 host tests passed independently of this
new failure. ADB root was used only for disposable-emulator diagnosis and then
disabled; the application itself continued running under its ordinary app UID.

### OS-backed ownership fix and verification

`SqliteStore` now opens the owner file with `OpenOrCreate`, exclusive sharing and
delete-on-close, retaining the handle for the workspace session. It never decides
ownership from marker contents or deletes a supposedly stale marker before trying
to acquire it. A same-filesystem temporary-file probe verifies actual exclusive
locking before opening the marker; this rejects the runtime's disabled-locking
configuration before touching an existing marker or database. Symlink markers are
rejected, and cancellation/write failures release any acquired lease.

The version 2 diagnostic marker uses `owner-pid=`, not legacy `pid=`, so old hosts
fail closed rather than interpreting its metadata as authority to unlink it.
Stop all hosts before upgrade; concurrent mixed-version upgrade is not supported.
Unix advisory locks still require cooperating applications and a trusted local
workspace directory. The implementation uses .NET's exclusive `OpenOrCreate` /
`DeleteOnClose` path, including its inode recheck during handoff; see
[the runtime implementation](https://raw.githubusercontent.com/dotnet/runtime/v10.0.0/src/libraries/System.Private.CoreLib/src/Microsoft/Win32/SafeHandles/SafeFileHandle.Unix.cs).
Operational recovery rules are in [operations.md](operations.md).

New `WorkspaceLeaseTests` cover a legacy marker whose PID is still alive, empty and
version-2 abandoned markers, a live legacy owner's exclusion before DB creation,
twelve simultaneous contenders, a real daemon killed after a durable mutation,
disabled runtime locking with an untouched marker, symlink rejection, and cancelled
initialization cleanup. Full build and all **166 tests** pass (148 application,
18 domain); `git diff --check` passes.

Device evidence:

- `lease-recovery`: the first incremental APK aborted before managed startup with
  mismatched compressed-assembly metadata. This is a failure, not a lease test pass;
  `startup-crash.txt` retains the abort. Cleaning generated Release output and
  rebuilding fixed packaging without changing trimming or app data.
- `lease-recovery-clean`: the fixed clean Release recovered the **untouched**
  failed legacy workspace automatically. Cold start, force-stop/relaunch and
  rotation passed with both identity fingerprints matching the earlier baseline.
- `lease-recovery-reboot`: Android reported boot completion before launcher
  resolution was ready. The harness now waits up to 30 seconds for that resolution.
- `lease-recovery-reboot-ready`: the same reboot, not a second reboot, passed with
  `previous_boot_id=09ef62b0-46de-47c5-993b-524817b2c543`,
  `observed_boot_id=714662e3-357c-4f1e-af69-bb0f3967dbd4` and
  `baseline_matched=true`. The cold-start PNG was inspected.

A disposable daemon at `/tmp/snook-lease-accept-qNkYTz/server` also passed CLI
`doctor` through a saved client profile without connection flags. The shared GUI
rendered its seeded Tasks surface at 980×640 against that daemon; the inspected
capture is `/tmp/snook-lease-accept-qNkYTz/images/tasks.png`. The separate client
directory contains only its profile, no SQLite database. This is headless GUI
verification, not native human or assistive-technology acceptance.

Clean partial-trim Release APK hashes (development signing, x64 runtime only):

```text
x64 Release   201d1c68a777939a843424b030467b3291fc1347559665c2dd05725dcf848215
arm64 Release b31d27b50e9e1ebc7cf6a65541c32116fa8b713a7218c383dce98cbc22fd8914
```

## Task-editor recovery extension

The independent host uses a dedicated `org.snook.task-editor.v1` Android Keystore
AES-GCM key and workspace-specific associated data for an open task-editor recovery
file. Unlike the original probe's 4096-byte secret bound, this store opts into a
32 KiB plaintext limit so every valid bounded task description and the remaining
editor fields fit; other Keystore users retain the 4096-byte default. The file is
private no-backup presentation state, expires after seven days and cannot authorize
a mutation by itself.

The API 36 x64 Debug verifier in
`/tmp/snook-android-editor-recovery6-20260920/evidence` confirmed that the saved
envelope is bounded and lacks the fixture plaintext, force-stop/relaunch produces a
different process and restores the exact draft without saving it, and Cancel removes
the file. A further process restart retained the original database task and did not
restore the canceled draft. This is emulator evidence only; power-loss, Keystore
loss, storage exhaustion, OS pressure and physical phone/tablet acceptance remain
pending.
