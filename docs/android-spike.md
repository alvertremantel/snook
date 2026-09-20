# Android platform spike and evidence

Status: Phase 0 in progress. This is the execution record for
[android-plan.md](android-plan.md), not approval of its architecture or a reduction
of the release scope. No Android release or feature-parity claim is made.

## Candidate build

`src/Snook.Android` is an isolated platform probe. It uses Avalonia 12 activity
startup and loads the existing UI resources. A clean install now remains
unconfigured until a native confirmation creates a disposable independent workspace
under Android's private no-backup directory; the visible daemon entry fails closed
because secure attachment is not implemented. The probe
checks SQLite initialization/reopen, competing-owner rejection, durable board
creation/rename, exact receipt replay after editing/reopening, generated bootstrap
JSON metadata, and an IANA DST gap/fold. Fixed probe operation IDs keep repeated
runs bounded to one board. A process-owned gate serializes checks across activities.
After success the probe deliberately retains a live store lease until the next
run or process death, and reports a hash of its synthetic workspace/board IDs for
identity comparison across process restart and reboot.
The [native feasibility extension](android-native-spike.md) now verifies Keystore
encryption and immutable receiver actions, including a protected-secret fingerprint
checked by the lifecycle harness. Its evidence and remaining security gates are
recorded separately to distinguish native primitives from completed product flows.
It persists only the confirmed independent development-host choice; it does not
connect to a daemon or implement product host switching.

The probe stays outside `Snook.slnx` so desktop contributors do not need the
Android SDK. Build it explicitly in addition to the mandatory repository checks.
Its ID `org.snook.spike` is deliberately a development identity; the production
application ID and signing custody remain release inputs. Candidate minimum API
26, target API 36, and arm64 are not yet device-validated. English is the current
probe language; production locale coverage remains open. Version code 1 is for
this disposable probe only; production codes must increase for every distribution.

The current host uses .NET SDK 10.0.111 and Android workload pack 36.1.2, with
centrally managed Avalonia.Android 12.1.2. Final SDK/workload locking follows the
feasibility result. The Android project keeps Release trimming enabled; no linker
warning suppression or global assembly roots have been added. The bootstrap
source generator does not prove the existing reflection-based daemon or persistence
serialization paths safe under trimming.
The workload currently resolves `TrimMode=partial` / `AndroidLinkMode=SdkOnly`;
successful publish is not evidence of full application-assembly trimming safety.

## Commands

Install the Android workload with `dotnet workload install android`. SDK/JDK setup
can use the workload's dependency installer (review the Android SDK licenses first):

```bash
dotnet build src/Snook.Android/Snook.Android.csproj -t:InstallAndroidDependencies \
  -p:AndroidSdkDirectory=/tmp/snook-android-sdk \
  -p:JavaSdkDirectory=/tmp/snook-android-jdk \
  -p:AcceptAndroidSdkLicenses=true
dotnet restore src/Snook.Android/Snook.Android.csproj
dotnet build src/Snook.Android/Snook.Android.csproj --no-restore \
  -p:AndroidSdkDirectory=/tmp/snook-android-sdk \
  -p:JavaSdkDirectory=/tmp/snook-android-jdk
```

Android commands require the workload resolver; the desktop solution continues to
use `-p:MSBuildEnableWorkloadResolver=false`. Override `RuntimeIdentifier` with
`android-x64` for an x64 emulator and restore for that identifier before building.
Never point the probe at a desktop workspace.

When switching RIDs or changing the trimmed assembly set, restore for the selected
RID, then run `dotnet clean` for that RID and Release configuration before publishing.
An incremental publish after removing a dependency produced mismatched compressed
assembly metadata and aborted before managed startup; a clean publish restored
correct startup. Do not work around this by disabling trimming. `clean` itself
requires assets restored for the selected RID. See the ownership-fix evidence in
[the native ledger](android-native-spike.md).

The Android project sets `SkipCopyUnchangedFiles=false`: signed packages carry
deterministic timestamps, so a size/time-only copy check can leave stale bytes in
`publish/` when a new APK has the same length. Compare the build-output and
published APK SHA-256 values before installing or distributing. This was detected
during the shared-workspace extraction; the earlier resource-check run installed
the stale published package and does not verify that extraction.

For a candidate Release, use a private development signing key and protected
password files. Do not commit those files, put literal passwords in shell history,
or use a production key for the spike:

```bash
dotnet publish src/Snook.Android/Snook.Android.csproj -c Release \
  -p:AndroidSdkDirectory=/tmp/snook-android-sdk \
  -p:JavaSdkDirectory=/tmp/snook-android-jdk \
  -p:AndroidKeyStore=true \
  -p:AndroidSigningKeyStore=/private/path/spike.keystore \
  -p:AndroidSigningKeyAlias=spike \
  -p:AndroidSigningKeyPass=file:/private/path/key-password \
  -p:AndroidSigningStorePass=file:/private/path/store-password
```

File-backed
password inputs cover both APK and AAB; the Android tooling does not support the
environment prefix for every AAB signing path.

### Emulator smoke test

Use a disposable virtual device. Install the SDK emulator and API 36 Google APIs
x86_64 image with `sdkmanager`; create a Pixel 6 AVD with `avdmanager`. On this host,
the image exceeded the `/tmp` quota, so the SDK was moved to ignored
`artifacts/android-sdk` with `/tmp/snook-android-sdk` retained as a symlink.
Set Java's temporary directory to disk-backed storage during image installation.
The AVD is under `artifacts/android-avd`. KVM is available **outside the sandbox**;
the earlier sandbox-only absence check did not establish host unavailability.

Emulator 37.1.11's SwiftShader GLES renderer crashed on this host before boot
(including with Vulkan disabled). A native backtrace identified `libGLESv2.so`.
The supported ANGLE mode booted successfully:

```bash
ANDROID_AVD_HOME="$PWD/artifacts/android-avd" ANDROID_HOME=/tmp/snook-android-sdk \
  /tmp/snook-android-sdk/emulator/emulator -avd snook-api36 \
  -no-window -no-audio -no-snapshot -no-metrics -gpu swangle \
  -memory 2560 -cores 2 -port 5554
```

For that emulator, restore/build/publish with `-p:RuntimeIdentifier=android-x64`.
Debug now embeds assemblies in the APK: the initial fast-deployment package failed
when installed by ADB alone. After `sys.boot_completed` reports `1`, install the
selected Debug or Release `org.snook.spike-Signed.apk` with
`adb -s emulator-5554 install --no-incremental -r <apk-path>`. Then run:

```bash
python3 scripts/verify-android-spike.py --serial emulator-5554 \
  --output artifacts/android-spike-new-run
```

The output directory must be new. The script targets only the already installed
`org.snook.spike`: it resolves its launcher, force-stops/relaunches it, presses the
actual UI button, requires a passing probe result, repeats after process restart
with an identical persisted identity fingerprint, and checks result retention
after rotation. It uses `wm user-rotation`, verifies actual hierarchy rotation 0
then 1, and supports both `free` and `lock N` preference formats. Writing only the
underlying settings keys was insufficient on this API 36 image.
It records XML, PNGs and a JSON report,
and restores the original rotation preferences even on failure. It does not clear
the test database. Review the screenshots; the script is not an accessibility or
full-feature parity test. Force-stop/relaunch is not OS eviction or reboot evidence.

Run the separate clean-install scenario only against this disposable package:

```bash
python3 scripts/verify-android-spike.py --serial emulator-5554 \
  --output artifacts/android-onboarding-new-run --onboarding
```

It clears `org.snook.spike` app data, proves that launch, daemon selection and
cancelled independent confirmation create no database, then confirms independent
creation and persistence across force-stop/restart. It is destructive to the
package's existing disposable workspace.

## Evidence and exit criteria

On 2026-09-19 the initial worktree was clean. The installed .NET SDK was 10.0.111;
no Android workload was initially present. Workload installation succeeded and
Android NuGet restore succeeded. An unsandboxed `adb devices -l` succeeded with an
empty device list. The initial Java installation lacked `jar`; SDK/JDK provisioning
was therefore required. No phone/tablet execution is established by these checks.

Provisioning subsequently installed Microsoft OpenJDK 17.0.14 and Android build
tools 36.0.0 under the disposable paths above. Debug packaging passed with zero
warnings/errors. Release publish completed with trimming and AOT enabled, producing
APK and AAB files under `bin/Release/net10.0-android36.0/android-arm64/publish`.
These initial artifacts use the tooling's **development debug signer**, not a
release-custody key. Debug APK signature verification passed v2 and v3.
The merged Release manifest has minimum 26/target 36, backup and cleartext disabled,
and no Internet permission for this local-only probe. The launcher is exported;
runtime/startup providers are private. AndroidX adds an exported profile installer
receiver protected by `android.permission.DUMP`; this needs to remain in the
release component inventory. Manifest inspection is not a device security test.

After initially adding the native AppCompat theme required by AvaloniaActivity, both builds
passed again. Final Release APK signatures verify as v2/v3; AAB JAR verification
passes with the expected self-signed development-certificate/no-timestamp warnings.
The manifest includes the activity theme and no debuggable flag. Initial arm64
artifact SHA-256 values, before later probe extensions (not release candidates):

```text
APK f2d61ef497e847d949ce955c7fd2cb2f6cb5099b5bc1ddb0ccd371e6b6e13eca
AAB 0261a0cb7729bd50244b0a44b1696efb5ce653e8383aa2e83f6b74c52240e8fd
```

The full desktop solution build passed with zero warnings/errors and all 149 tests
passed (131 application, 18 domain). No desktop UI code was changed.

### API 36 emulator execution (2026-09-19)

The expanded probe passed on API 36 x86_64 in Debug and partial-trim/AOT Release,
using development signing. The Release package replaced Debug without clearing
the private test workspace. The repeatable harness passed cold start, force-stop/
relaunch and portrait-to-landscape rotation. The startup and result screenshots
were inspected: shared resources render, text wraps, and the 48 dp run action and
result remain reachable at 720×1280 and 1280×720, density 240. These are probe-only
screens, not adaptive product UI acceptance.

Evidence is in `artifacts/android-spike-api36`, with Release scenario reports,
XML and PNGs in `release-lifecycle`. Emulator version is 37.1.11, image is API 36
Google APIs x86_64 revision 7. The checks verify native SQLite loading, initialization,
single-owner refusal, mutation/receipt behavior after edit and backend reopen,
bootstrap JSON metadata and IANA gap/fold behavior. No physical device, low-memory
OS eviction, interrupted transaction, backup/restore, notification, or tailnet test
has passed.

Initial executed x64 APK SHA-256 values, before retained-owner verification:

```text
Debug   821d05d2b5333268df5469e95489f1620a8e0032b2fcd14422213cf2a8c742ce
Release 058abf142573f3cbbffe53fda101daa0a566b676f0c765fd648aef4e9653021b
```

The strengthened Release probe subsequently passed force-stop/relaunch while
holding the live ownership lease. Workspace/board identity fingerprints matched
before and after process death. An actual `adb reboot` of this disposable emulator
was then performed with the lease held; after observing boot completion, the
harness passed again. Comparing the pre-reboot and post-reboot report fingerprints
with `jq -e` returned `true`. The first 45-second boot observation expired, but the
same boot continued and completed; the emulator was not restarted to hide a failure.

Evidence for the retained-owner check is in `release-owner-recovery`; the final
strict rotation and reboot run is in `release-reboot-verified`. The final portrait
and landscape screenshots were inspected. This proves the small independent probe's
stale-owner/reopen path on this image, not the product's entire lifecycle matrix.
The emulator was stopped after verification; its disposable AVD/data remains for
later spike work. Updated APK SHA-256 values:

```text
x64 Release   f86313ce228a9c948ab5ce5351b3153d7a80aee2c12a79c4115593137193d6dc
arm64 Release 40fce4c55e472b823f86d237f0202d86024238cd8ea974f180ae7c74ef8b0ade
```

The updated arm64 Release publish also passed; execution remains x64-only.

The full restore/build/test sequence and `git diff --check` passed again after the
probe changes (149 tests). Physical phone/tablet, minimum-API and other matrix
coverage remain pending; Phase 0 has not exited.

Record each result with commit, configuration, tool versions, device/API/ABI,
artifact hashes and retained logs. Every row is required before Phase 0 exits:

| Gate | Required evidence | Current state |
| --- | --- | --- |
| Debug and trimmed signed Release | APK/AAB builds, zero unresolved linker warnings, signature/manifest inspection | Debug and partial-trim Release built and run on API 36 x64 emulator; development signing only |
| Native loading and resources | On-device SQLite/reopen, AXAML, JSON, time-zone probe | API 36 x64 emulator passed; physical/minimum-API pending |
| Reflection preservation | All daemon dispatch/DTO shapes exercised in trimmed build | Pending |
| Process ownership | Activity recreate, competing owner, process kill and reopen | PID-reuse defect fixed with OS locks; untouched failed workspace recovered on API 36 x64, then restart/rotation/reboot passed with baseline identities. Host concurrency/crash/disabled-lock tests pass; physical/OS eviction/interrupted transaction matrix pending |
| Phone and tablet | Physical install/launch for both, emulator baseline | API 36 emulator baseline passed; physical acceptance explicitly left pending by the user (phone available, no tablet); no physical-device evidence |
| Native feasibility | Notification receiver, Live Update fallback, widgets, Keystore, SAF | API 36 x64: Keystore, synthetic receiver/actions, committed timer notification/actions, promoted-ongoing request with ordinary fallback, permission denial/recovery, and SAF backup/export/restore verified; actual promotion/demotion, widgets and physical-device security pending |
| Tailnet boundary | Serve/TLS, identity, denied device, public/Funnel rejection, SSE | Threat model drafted; tests pending |
| Adaptive prototype | Phone/tablet/compact-height captures and draft retention | Shared activity session and broad compact feature layouts are implemented; API 36 compact Month week navigation/day reachability and desktop compact/expanded captures pass. Physical tablet/foldable and assistive-technology acceptance remain pending |
| Release inputs | Production identity, key custody, version policy, floor/ABIs/locales | Candidate values only |
| Quality budgets | Measured launch, memory, frames, size, network/wakeups, 24-hour battery | Pending |

Phases 1–5 and the entire acceptance matrix in the implementation plan remain
required. Passing the local probe is only one part of Phase 0.

## References

- [Avalonia 12 Android lifetime changes](https://docs.avaloniaui.net/docs/avalonia12-breaking-changes)
- [Avalonia Android toolchain setup](https://docs.avaloniaui.net/docs/platform-specific-guides/android)
- [Android publishing and signing](https://docs.avaloniaui.net/docs/deployment/android)

## Shared workspace host foundation

The full workspace AXAML and interaction handlers now live in `WorkspaceView`, a
platform-neutral Avalonia `UserControl`. `MainWindow` is the desktop wrapper for
lifetime, connection dialogs and screenshots. Hosts supply the existing view model;
detaching a view removes its subscriptions, resize timer and keyboard routing but
does not dispose the session/backend or cancel its draft. Template ancestor
bindings now target `WorkspaceView` instead of `Window`. Root compiled bindings
remain disabled; individually typed templates keep their existing opt-in policy.

Verification on fresh disposable desktop profiles:

- `/tmp/snook-workspace-view-final-fBTO7Q/images`: the combined seeded 980×640
  interaction run passed Today/task capture, actual view recreation, task batch
  conflicts, board menus and pointer movement, task drawers, habits, journals,
  timers, calendar planning and settings editors/deleted catalogs.
- `/tmp/snook-workspace-view-empty-PgiKOR/images`: separate empty Today, task list/
  board, habits, journals, tracker, calendar and settings captures passed.
- Draft-recreation, drag/drop, empty task/calendar and settings-conflict PNGs were
  inspected. These are headless checks, not native human or accessibility acceptance.

The initial review exposed Escape losing its route after a focused editor control
was removed. The shared control now attaches keyboard fallback to its `TopLevel`
and removes it when detached. Fresh isolated board/settings checks and the final
combined pass verify the fix. The drag verifier also now scrolls the page through
the real UI before sending pointer coordinates at minimum height; it no longer
clicks an initially clipped handle.

Use `SNOOK_SCREENSHOT_VERIFY_HOST=1` with seeded `tasks-list` to recreate the view
while retaining an unsaved editor draft, refresh from the backend, and verify
Escape discards without saving. It emits `workspace-recreated.png`.

The desktop solution builds without warnings and all 166 tests pass. Android x64
partial-trim Release publishing succeeds. During package checking, the existing
`publish/` APK had stale bytes despite matching the build output's timestamp/size;
the `shared-workspace-resources` run therefore does not validate this change.
The project now forces package copying; current build and published APK SHA-256
values both equal `b61c7f3411acaa230fd7b84cae356559d1bcc633e484a5b1fbe039148682d448`.
The hash-matched package passed the existing Android cold-start, force-stop/relaunch
and rotation probe with preserved workspace/secret identities; evidence is in
`artifacts/android-spike-api36/shared-workspace-verified-package`. This validates
shared style/resource startup and existing native probes, not the full workspace
control running inside an Android activity.

This extraction did not itself wire the workspace into Android; the subsequent
development host below does. Compact/expanded navigation/layout, Android Back
handling and process-death draft restoration remain required. This foundation
does not claim Android UI feature parity or completion of Phase 0/1.

### Independent session ownership foundation

`Snook.Application.WorkspaceSession` now serializes independent startup and close,
reads a committed bootstrap before reporting readiness, and returns the same live
backend to repeated callers. Its factory owns partial-startup cleanup; the session
owns a successfully returned backend, including cleanup when the readiness read
fails. Observer failures cannot interrupt ownership transitions. Queued caller
cancellation does not cancel another caller's startup. Close waits for startup,
releases the owner, and rejects later opens. A cleanup failure prohibits retry.

Five application tests exercise concurrent opens, observer isolation, explicit
retry, cancelled waiters, close/startup ordering, and cancelled-candidate lease
release and borrowed projection disposal using disposable SQLite workspaces.
Attached, stale, and locked states are reserved, not implemented; there is no
attached-to-independent fallback. At this foundation stage, encrypted process-death
draft recovery and foreground/background coordination remained required. Physical phone and
tablet/foldable acceptance remain pending.

### Android activity workspace host

The application now owns an `AndroidWorkspace` containing one `WorkspaceSession`
and one shared `MainWindowViewModel`. Every activity receives a new host/view but
borrows that same model, including open drafts. **Open independent workspace**
opens `NoBackupFilesDir/independent/workspace.db`, separate from the synthetic
platform probe. **Platform checks** detaches the workspace view without disposing
its model. This initial host did not yet persist selection or drafts across process
death; later sections record host selection plus quick-capture and task-editor
recovery.
No external connection profile or path is accepted by this development entry point.

The x64 partial-trim Release build and published APK hashes match:
`bd3bb2aeb359d2136f664c5b512a726f081481ac7025f52ec283c08889e5722d`.
`artifacts/android-spike-api36/activity-workspace-retention-verified` records the
native regression checks plus `--workspace`: open the shared workspace, enter an
unsaved capture draft, rotate and verify that exact text remains in the same
process. Android lifecycle events independently showed MainActivity destroy/create
pairs in process 6049 at 20:48:17 and 20:48:28 on 2026-09-19. The earlier
`activity-workspace-retention` attempt timed out on a missing automation label,
not a startup failure: Android exposes the input placeholder instead of the
explicit desktop automation name. That accessibility discrepancy remains open.

The inspected portrait capture is severely cramped by the desktop sidebar and
split panes. It is evidence of host/retention plumbing, not an acceptable mobile
layout. Safe-area/IME policy, Android Back, adaptive layouts, background work
suppression, and durable drafts remain required. Desktop seeded/empty 980×640
captures and the host recreation interaction check passed in fresh profiles;
the full desktop build has zero warnings/errors and all 171 tests pass.

### Compact navigation and Today pass

`WorkspaceView` now reacts to available width below 600 dp: it hides the sidebar,
shows Today/Tasks/Timer/More bottom navigation, stacks Today panes, moves search
below the header, and fits the editor drawers to the viewport. These are layout
changes to the same controls/model, not replacement drafts. The desktop host check
now also narrows to 480 dp with an open editor and widens again, asserting draft
identity/text and navigation changes. Seeded/empty desktop captures were inspected
in `/tmp/snook-compact-seeded-tcaSwr` and `/tmp/snook-compact-empty-ckqtEn`.

The x64 Release APK build/publish SHA-256 is
`f3231a82fdcaf63dab56d7bb33ec617eba1974fa1a0e0832ea1908fa2c3f37f6`.
`artifacts/android-spike-api36/compact-navigation` records native probe regression,
workspace rotation, More → Habits → Today, and exact unsaved capture retention.
The portrait Today and More captures were inspected: the squeezed desktop columns
are gone on Today. The verifier's `--workspace` option now includes this compact
navigation check and expects a device viewport below 600 dp in portrait.

This does not validate the remaining pages' compact layouts, medium-width rail,
200% font scaling, every touch target, tablet/fold behavior, or physical devices.
Active timer placement and other dense Today rows still need mobile refinement.
All 171 desktop tests, full build and diff checks pass. Physical-device acceptance
remains pending by user direction.

### Compact Today action order

The next pass separates the four primary cards so compact order is capture,
timers, next tasks, then agenda, without reparenting/replacing their controls.
Wide windows use two aligned card rows. Next-task actions move below their text
in compact mode, Today Stop has a text label, and compact buttons have 48 dp
minimum width/height. The host check asserts card ordering and action rows with
a seeded fixture, alongside draft retention. Inspected captures are in
`/tmp/snook-today-actions-final-1HGGvQ` and the separate empty profile
`/tmp/snook-today-actions-empty-AxAcxM`.

The x64 Release build and published APK both hash to
`8dcf4f62de4c5c48cc17331b0f3bf6ce9a22aa196f3ccffa6045dcc3f135ce5a`.
The installed package passed native probes, rotation/draft retention and compact
navigation in `artifacts/android-spike-api36/compact-today-actions`; the returned
portrait Today capture was inspected. All 171 tests, full build and diff checks
pass. These checks do not establish complete page adaptation, system font-scale
or assistive-technology behavior, predictive Back, or physical acceptance.

### Compact Tasks capture and list pass

Below 600 dp, Tasks puts board creation below its wrapping view toolbar, places
the project picker/Add action below a full-width capture title, and moves task
list actions below task text. Metadata wraps; board tabs retain explicit scroll
arrows. The board/project creation form width is bounded by available view width.
The host check verifies compact placement, retained capture text and restoration
of the wide layout. Inspected seeded/empty evidence is in
`/tmp/snook-compact-tasks-9gNGg0` and `/tmp/snook-compact-tasks-empty-1s8Bah`.

The x64 Release APK build/publish hash is
`f2a95df4659ca6b0703b6bbe3376da1092dd66987a95de4dd00cad93d143cfff`.
The installed package passed the extended `--workspace` check in
`artifacts/android-spike-api36/compact-tasks`: native probes, rotation, More
navigation, and Today → Tasks with exact capture text retained and the project
picker measured below the title. The native Tasks portrait capture was inspected.
All 171 tests, full build and diff checks pass. Board lane adaptation, short-height
and IME popup reachability, remaining editors, and physical acceptance remain open.

### Medium-width navigation rail

Widths from 600 to below 840 dp now use an 88 dp rail, reusing all nine navigation
buttons with visible icons/labels and 64 dp height. Lower destinations remain in
the vertical scroll. The full sidebar timer card is hidden, while host/status text
remains in the workspace footer. Today/Tasks stay stacked at medium width. At 840
dp the full sidebar returns; below 600 dp navigation remains at the bottom.

The headless host check exercises 599/600/839/840 dp transitions without losing
capture text, checks nine vertical rail items, and scrolls Settings fully inside
the viewport. Inspected final captures are in
`/tmp/snook-medium-reach-final-UNrDYT`; separate empty wide captures are in
`/tmp/snook-medium-empty-KqpQLK`. Selected-rail contrast was corrected during review.

The x64 Release build/publish SHA-256 is
`e85f9c4202bec11baf7e9fd6c4e6f11fd0c5906213d90dba9acd7503fb74e5a8`.
`artifacts/android-spike-api36/medium-rail` passed the native regression and
workspace checks; its inspected landscape capture visibly uses the rail, and
rotation to compact mode retains the draft. Full build, all 171 tests and diff
checks pass. This does not establish tablet/foldable hardware acceptance,
font-scaling/accessibility acceptance, or adaptive completion of other pages.

### Foreground/background presentation ownership

The Android application tracks started activities independently of view factories.
The workspace presentation is active only when an activity is visible and the
workspace, rather than platform probes, is selected. Hiding stops its display timer
and skips new backend-notification refresh work without releasing SQLite ownership
or changing persisted timer state. Resume refreshes committed state; capture and
editor drafts remain owned by the same model. The display timer uses the injected
TimeProvider. Queued display ticks are guarded after hiding/disposal, and bootstrap
results from a previous visibility generation are discarded.

Three application tests cover timer disable/re-enable, hidden changes, hidden
startup, retained capture, disposed no-restart behavior, and an intentionally
delayed old bootstrap arriving after fresh resume. The headless host check now
also hides an open editor, changes the backend, resumes, and checks the same draft.
Seeded/empty captures were inspected in `/tmp/snook-lifecycle-editor-Ww5iOj` and
`/tmp/snook-lifecycle-empty-SUVCi2`. All 174 tests, full build and diff checks pass.

The final x64 Release build/publish hash is
`d476eb280d5ad589ab325c4ebc5657b989c16cc442de1caa930244a6fd1bc034`.
`artifacts/android-spike-api36/presentation-lifecycle-final` passes the native
regression, rotation/navigation/capture checks, and Home/background/return with
the same process and draft. The foreground-return capture was inspected. The
earlier `presentation-lifecycle` pass predates the delayed-bootstrap guard.

Already-running page requests and mutations are not cancelled by this first
visibility pass; their completions can still run. Per-page request cancellation,
coalescing and stale-response checks need broader coverage. This is not an
OS-eviction, process-death draft recovery, battery, native background-service,
attached transport, or physical-device acceptance result.

### Page reads across visibility changes

The primary page loaders now join the lifecycle invalidation boundary: Tasks,
Today, History, Summary, Calendar, Habits, and journal catalog/entries. Each loader
skips hidden startup and rejects obsolete results. Multi-read pages check between
requests, so an obsolete result cannot trigger the remainder of its read chain.
History/journal pagination resets on visibility transitions and disposal. Today
stages upcoming/recent rows until its reads complete, and History retains existing
rows until a valid replacement is ready. Mutations and editor-owned drafts are
not cancelled or replayed by this mechanism.

Eight delayed-response cases verify hidden results do not publish or continue
queries and that a subsequent resume loads committed changes. Full build, all
182 tests, editor-retention checks and diff checks pass. Inspected captures are
in `/tmp/snook-page-lifecycle-yvU60r` and the separate empty Today/History profile
`/tmp/snook-page-lifecycle-empty-UiaIKg`.

The x64 Release build/publish SHA-256 is
`04ab7644a19ff4a43be88dfa5517aa5fd774cc077379709c3bde801dd083fa16`.
The installed package passed native probes, rotation/navigation/capture and
Home/return in `artifacts/android-spike-api36/page-lifecycle`; its foreground-return
capture was inspected. This is response invalidation, not cancellation of an
already-running transport request. Request coalescing, editor-specific in-flight
operations, native background work and physical acceptance remain separate work.

### Cooperative presentation read cancellation

Primary presentation reads now carry a visibility-scoped cancellation token,
including calendar bootstrap/reload and paged calendar history. Hiding or disposal
cancels the scope; resume creates a new one. Navigation loaders consume expected
cancellation quietly. Generation/version checks remain in place for requests
whose backend does not cooperate. Mutation requests never receive this token.

Eight new navigation cases verify token cancellation through Tasks, History,
Summary, Calendar, Habits and Journals. A ninth holds a task-creation response
after commit, hides the workspace, and verifies that its request is not cancelled
and the single created task remains. The existing non-cooperative delayed-read
cases still pass. Full build, all 191 tests and diff checks pass. Inspected seeded
editor/calendar captures are in `/tmp/snook-cancel-reads-q5jGTZ`, with a separate
empty calendar profile in `/tmp/snook-cancel-reads-empty-WRvL4i`.

The x64 Release build/publish SHA-256 is
`0cef44e59c4a6cf42e71e446b2fc4f502905b9e278b92d756a34b3153b037b58`.
The installed package passed the native, rotation/navigation and Home/return
regressions in `artifacts/android-spike-api36/read-cancellation`; its return capture
was inspected. Cancellation is cooperative, not proof that every platform/backend
stops work immediately. Request coalescing, editor-specific reads, native background
integration and physical-device acceptance remain open.

### Refresh-time task-picker lifecycle gap

A follow-up audit found that the manual-task picker query inside the bootstrap
refresh still lacked both the visibility token and a post-await generation check.
It now observes the same boundary as the primary page reads, so a response arriving
after hide/dispose cannot replace picker options or continue the remaining refresh.
Four regression cases cover cooperative and non-cooperative backends with hide and
dispose; resume in the hide cases loads the committed task into fresh options.

Full build and all 195 tests pass. Fresh seeded/empty 980x640 captures in
`/tmp/snook-picker-review-kTkjaR` were inspected, and the workspace/editor-retention
check passes. The x64 trimmed Release publish passed with matching build/publish
APK SHA-256 `3b09d5dfbe38d057ab547961e0117bb8f4858afb5afa52e51cc941b293b6df12`.
The installed package passed native probes, rotation, compact navigation and
Home/return in `artifacts/android-spike-api36/task-picker-lifecycle`; the return
capture was inspected and the existing workspace/secret fingerprints matched.
Physical acceptance remains pending as requested: the user has a phone but no
tablet. This fix does not complete request coalescing, durable process-death drafts,
or the remaining Android feature and release gates.

### Visibility-scoped refresh coalescing

Full refresh requests now share one running batch per visibility generation.
Requests arriving during a pass set one pending trailing pass instead of starting
overlapping bootstrap chains. Awaiting callers share completion of the batch.
Hiding/disposal prevents pending old work from running; a new visible generation
starts independently even if an old backend ignores cancellation.

The burst regression holds a bootstrap response while 30 search changes and an
explicit refresh arrive, then verifies exactly one trailing bootstrap. The delayed
old-generation regression now queues pending work before hide and verifies it does
not issue further reads after fresh resume. Full build and all 196 tests pass.
Seeded/empty 980x640 captures in `/tmp/snook-refresh-review-1euoAu` were inspected;
the workspace/editor-retention check passes. Android x64 trimmed Release publish
passes with matching build/publish APK SHA-256
`77f6598288da519650899fd4a99428c1d1dc241c788ef3e06572ee71cee81343`.
The installed package passed native probes, rotation, compact navigation and
Home/return in `artifacts/android-spike-api36/refresh-coalescing`, with matching
persisted workspace/secret fingerprints. The return capture was inspected.

This is full-refresh coalescing, not a universal queue for page/editor queries or
mutation operations. Physical-device acceptance and remaining product/release gates
remain pending.

### Workspace-first entry and share regression recovery

Android now opens Today in its independent workspace on normal launch. The compact
on-device host bar puts development probes behind Tools. An explicit component
launch with no action/data is recognized as a normal launch, fixing the false
share-rejection banner that broke the earlier `share-entry` emulator run.

Plain-text task sharing accepts one pending input of at most 300 characters, with
ten-minute expiry and no persistence. Review fills quick capture without saving;
an existing capture or editor prevents replacement, and Discard clears only the
pending share. Seven shared tests cover input bounds, invalid payloads, replacement
protection, discard and expiry. At this milestone the complete authenticated intent
router, long-text/journal sharing and encrypted process-death draft recovery remained
open; the later unified-router and encrypted-recovery milestones supersede those gaps.

The development Debug build passed the emulator run in
`artifacts/android-spike-api36/workspace-first`: direct Today startup, platform
probes, rotation/navigation/Home-return, cold/warm share review, preservation of an
existing capture, no consumed-share redelivery on rotation, and rejection of a VIEW
URI. Startup and share captures were inspected. Shared build and all 203 tests pass;
the first suite run had a daemon-watch timeout, and a full rerun passed. This turn
used a development build for interaction checks, not a distribution milestone.

### Encrypted quick-capture process recovery

Quick capture now uses a private Keystore-encrypted, workspace-bound recovery file.
Title/project changes debounce for 400 ms and backgrounding requests a flush. Before
task creation, the exact request identity is checkpointed; failure prevents dispatch.
The bounded snapshot expires after seven days. Restoring never submits a task and
preserves exact retry identity if the earlier response was lost. Unavailable recovery
is reported rather than overwritten; clearing capture removes its saved copy.

Full build and all 206 tests pass, including checkpoint failure before dispatch and
restored exact retry after a committed response is lost. The Debug emulator run in
`artifacts/android-spike-api36/encrypted-capture-verified` passed: the private file is
bounded and does not expose fixture text, a different app process restores the
capture, and clearing it removes the file and leaves a subsequent restart empty.
Recovery and cleared-restart captures were inspected. The preceding run stopped on
a transient UI Automator hierarchy failure; bounded fresh-hierarchy retries now
re-observe the same app rather than using stale XML.

At this stage only quick capture was persisted. Task-editor recovery is covered by
the following extension. Utility editors, the latest pre-checkpoint keystrokes,
power-loss durability, low-storage/key-loss end-to-end cases, and the broader device
matrix remain outside this result. The `--capture-recovery` check requires Debug
`run-as` access and a disposable workspace and runs separately from `--share`.

### Encrypted task-editor process recovery

The independent host now checkpoints one open task editor to a separate private
no-backup file after 400 ms and on backgrounding. Android Keystore AES-256-GCM uses
a distinct alias and workspace-specific associated data. The versioned plaintext is
bounded to 32 KiB, expires after seven days, and holds the task identity, expected
revision and unsaved task fields. It is device-local presentation recovery, not a
workspace record or mutation request.

Restoration reloads the authoritative task, opens the normal editor, overlays the
saved fields and shows a recovery notice. It never presses Save. Keeping the saved
expected revision means an external update still reaches the ordinary revision-
conflict path. Cancel and successful Save remove the file; unreadable recovery is
reported and protected from overwrite until explicit discard.

The API 36 x64 Debug run in
`/tmp/snook-android-editor-recovery6-20260920/evidence` verified a bounded encrypted
file with no fixture plaintext, a different process ID after force-stop/relaunch,
the exact restored title, and no automatic mutation. Cancel removed the file; a
second process restart showed the original persisted task and no recovered draft.
Both final PNGs were inspected. Fresh desktop task-drawer checks in
`/tmp/snook-task-editor-recovery-desktop-20260920-images` also passed draft retention,
persisted save, conflict preservation and Cancel/Escape behavior.

This covers the task editor only. Utility editors, the newest keystrokes before a
checkpoint, power-loss/low-storage/key-loss cases, OS eviction under pressure and
physical phone/tablet acceptance remain pending.

### Workspace navigation process recovery

The independent host now stores a separate versioned navigation snapshot under
private no-backup storage. It is bound to the workspace ID, limited to 2048 bytes,
written through serialized flushed replacement after 250 ms and on backgrounding,
and contains no search text, capture/editor content, credential or mutation request.
It covers the current destination, task view/sort/board, calendar mode/view/date,
summary grouping, Settings category and journal selection. Invalid state is ignored;
missing board or journal IDs fall back through authoritative workspace loading.
Recovered task editors still take precedence and route to Tasks.

Three shared tests cover codec bounds and workspace identity, full subview restoration
and a missing-board fallback. The updated API 36 x64 Debug run in
`/tmp/snook-android-navigation-recovery-final-20260920/evidence` inspected the bounded file,
verified it contained no capture text, and restored Tasks together with an encrypted
capture in a different process. Clearing the capture and restarting again retained
Tasks without resurrecting the draft. The restored and cleared screenshots were
inspected. This is process-restart evidence on one emulator, not recent-task/OS-
pressure, every destination, physical-device or accessibility acceptance.

### Committed timer notifications and reboot reconstruction

The independent development host now projects committed running/paused sessions to
ordinary Android notifications with a bounded shared projection, system chronometer,
redacted public version and Keystore-authenticated opaque Pause/Resume/Stop receipts.
Content opens the validated Time tracker destination. Actions survive ordinary
process eviction and reopen the shared backend without a client-side timer engine.

A non-exported, credential-protected system receiver handles completed boot,
this-package replacement, wall-clock change and time-zone change. It never creates
an unselected workspace and does not register for locked boot. The API 36 x64
`--timer-notification` run in
`/tmp/snook-android-timer-boot-Final03/evidence` passed permission education, the
private/public surface, process-death Resume, an actual different completed boot,
notification/action reconstruction, post-boot Stop and encrypted-record cleanup.
The post-boot and removed captures were inspected. The merged manifest contains the
single `RECEIVE_BOOT_COMPLETED` permission and marks both timer receivers
`exported=false`; the system receiver is `directBootAware=false` with exactly four
actions. The API 36 run in `/tmp/snook-android-live-update-Final02/evidence` also
confirmed the Android 16 promoted-ongoing request on running and post-boot surfaces,
its absence while paused and the normal fallback when the image neither promotes
nor exposes the access-settings activity. Native delivery of the other three system
events, actual promoted/demoted treatment, reminders, widgets, custom artwork,
attached mode and physical-device acceptance remain open.

### Unified cold/warm activity entry router

Normal launch, plain-text task share and timer-notification content open now pass
through one host-independent fail-closed classifier from both `OnCreate` and
`OnNewIntent`. The Android adapter extracts only primitive metadata. A share requires
exact `SEND`/`text/plain`, one bounded text extra and no URI, stream, ClipData, token
or unknown key. Timer content requires the exact internal action, one matching
uppercase-hex 256-bit token and `snook-internal://timer/<token>` without port, query
or fragment. The encrypted timer-action store and current backend state remain the
second authorization gate. Unknown actions, malformed parcels, payload-bearing
launcher intents and substitutions produce explicit rejection without navigating,
saving or replacing a draft.

Fourteen shared classification cases cover normal launch, exact/boundary share,
alternate MIME, URI/stream/ClipData/extra substitution, exact timer open, malformed
tokens, path/query/fragment/port substitution and unknown/payload-bearing launch.
The API 36 x64 Debug run in
`/tmp/snook-android-entry-router-final-20260920/evidence` passed the existing cold/
warm share, rotation, navigation and lifecycle scenarios plus unexpected share
extras, a `VIEW file:` URI and a forged timer-open extra. The rejection screenshots
were inspected and retain the existing Tasks draft. The Android build and full
solution build have zero warnings/errors; all 246 shared tests pass (228 application,
18 domain). Shortcuts, widgets, external app/deep links and physical-device
acceptance remain separate work.
