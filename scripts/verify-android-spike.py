#!/usr/bin/env python3
"""Exercise an already installed, disposable org.snook.spike via Android UI.

This is a Phase 0 smoke test, not the Android product acceptance suite. It never
installs/uninstalls apps or targets the production application. The explicit
--onboarding scenario clears only the disposable development package's app data.
"""

import argparse
import json
from pathlib import Path
import re
import sqlite3
import subprocess
import time
import uuid
import xml.etree.ElementTree as ET


PACKAGE = "org.snook.spike"


class HierarchyUnavailable(RuntimeError):
    pass


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--serial", required=True, help="Explicit ADB device serial")
    parser.add_argument("--output", required=True, type=Path, help="New evidence directory")
    parser.add_argument("--adb", default="adb")
    parser.add_argument("--workspace", action="store_true",
                        help="Also open the shared workspace and check in-memory capture retention across rotation")
    parser.add_argument("--onboarding", action="store_true",
                        help="DESTRUCTIVE to disposable org.snook.spike only: clear its app data and verify explicit first-run host selection")
    parser.add_argument("--timer-notification", action="store_true",
                        help="DESTRUCTIVE to disposable org.snook.spike only: clear its app data and verify a real timer notification and actions")
    parser.add_argument("--share", action="store_true", help="Check plain-text share review and existing-draft protection (requires --workspace)")
    parser.add_argument("--capture-recovery", action="store_true", help="Check encrypted capture and task-editor recovery using a Debug app and run-as (requires --workspace)")
    parser.add_argument("--editor-back", action="store_true", help="Run only native editor Back/confirmation/rotation checks; never saves the draft")
    parser.add_argument("--backup-export", action="store_true", help="Export the disposable workspace through the Downloads system picker and check cancellation/read-back")
    parser.add_argument("--document-export", choices=("JSON", "CSV"), help="Check a native JSON or CSV export independently using the disposable Downloads picker")
    parser.add_argument("--backup-review", help="Review this existing backup filename in disposable Downloads; never restores or saves workspace data")
    parser.add_argument("--backup-restore", help="DESTRUCTIVE to disposable org.snook.spike only: restore this Downloads backup from a different workspace and verify identity after restart")
    parser.add_argument("--after-boot-id", type=uuid.UUID,
                        help="Wait for a completed boot different from this previously observed kernel boot ID")
    parser.add_argument("--baseline-report", type=Path,
                        help="Require workspace and protected-secret identities to match a prior report")
    args = parser.parse_args()
    independent_runs = sum((args.onboarding, args.timer_notification, args.editor_back, args.backup_export, bool(args.document_export), bool(args.backup_review), bool(args.backup_restore)))
    if independent_runs > 1 or (independent_runs and (args.workspace or args.share or args.capture_recovery or args.after_boot_id or args.baseline_report)):
        parser.error("Editor Back and document exports each run independently of the other scenarios")
    review_file = args.backup_review or args.backup_restore
    if review_file and not re.fullmatch(r"[A-Za-z0-9._-]{1,128}\.snook-backup", review_file):
        parser.error("--backup-review requires one simple .snook-backup filename already in Downloads")
    if args.share and not args.workspace:
        parser.error("--share requires --workspace")
    if args.capture_recovery and not args.workspace:
        parser.error("--capture-recovery requires --workspace")
    if args.capture_recovery and args.share:
        parser.error("Run --capture-recovery and --share separately; each owns its capture fixture")
    args.output.mkdir(parents=True, exist_ok=False)

    def adb(*command, binary=False):
        return subprocess.run(
            [args.adb, "-s", args.serial, *command], check=True,
            stdout=subprocess.PIPE, stderr=subprocess.PIPE,
            text=not binary, timeout=30,
        ).stdout

    def private_file_exists(path):
        completed = subprocess.run(
            [args.adb, "-s", args.serial, "shell", "run-as", PACKAGE, "test", "-f", path],
            check=False, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, timeout=30,
        )
        if completed.returncode not in (0, 1):
            raise RuntimeError("The disposable package's private storage could not be inspected.")
        return completed.returncode == 0

    def process_id():
        completed = subprocess.run(
            [args.adb, "-s", args.serial, "shell", "pidof", PACKAGE], check=False,
            stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, timeout=30,
        )
        if completed.returncode not in (0, 1):
            raise RuntimeError("The disposable package process state could not be inspected.")
        return completed.stdout.strip()

    def tree():
        status = adb("shell", "uiautomator", "dump", "/data/local/tmp/snook-spike-ui.xml")
        # UI Automator can report a null root/idle failure with exit code zero,
        # leaving the previous XML on disk. Never treat that stale file as evidence.
        if "dumped to: /data/local/tmp/snook-spike-ui.xml" not in status:
            raise HierarchyUnavailable("UI Automator did not produce a fresh hierarchy.")
        data = adb("exec-out", "cat", "/data/local/tmp/snook-spike-ui.xml", binary=True)
        return data, ET.fromstring(data)

    def wait_for(predicate, description):
        deadline = time.monotonic() + 60
        last_data = None
        while time.monotonic() < deadline:
            try:
                data, root = tree()
            except HierarchyUnavailable:
                time.sleep(1)  # Re-observe the same app; never consume the old XML.
                continue
            last_data = data
            failures = [n.get("text", "") for n in root.iter("node")
                        if n.get("resource-id") == "ResultText" and n.get("text", "").startswith("FAIL")]
            if failures:
                capture("failure", data)
                raise RuntimeError(failures[0])
            if predicate(root):
                return data, root
            time.sleep(1)
        if last_data is not None:
            capture("timeout", last_data)
        raise TimeoutError(description)

    def result(root):
        return next((n.get("text", "") for n in root.iter("node")
                     if n.get("resource-id") == "ResultText"), "")

    def capture(name, data):
        (args.output / f"{name}.xml").write_bytes(data)
        (args.output / f"{name}.png").write_bytes(adb("exec-out", "screencap", "-p", binary=True))

    def capture_screen(name):
        (args.output / f"{name}.png").write_bytes(adb("exec-out", "screencap", "-p", binary=True))

    def open_notification_shade():
        # System UI can ignore the first expansion while an activity/process or
        # transient toast is settling. Reissue expansion against the same state.
        adb("shell", "input", "keyevent", "82")
        adb("shell", "cmd", "statusbar", "collapse")
        time.sleep(0.25)
        adb("shell", "cmd", "statusbar", "expand-notifications")
        time.sleep(1)
        adb("shell", "cmd", "statusbar", "expand-notifications")
        time.sleep(1)

    def named(root, label):
        return next((n for n in root.iter("node")
                     if n.get("text") == label or n.get("content-desc") == label), None)

    def tap(node):
        if node is None or node.get("enabled") != "true":
            raise RuntimeError("The workspace action is missing or disabled.")
        bounds = [int(v) for v in re.findall(r"\d+", node.get("bounds", ""))]
        if len(bounds) != 4 or bounds[2] <= bounds[0] or bounds[3] <= bounds[1]:
            raise RuntimeError("The workspace action is not reachable.")
        adb("shell", "input", "tap", str((bounds[0] + bounds[2]) // 2), str((bounds[1] + bounds[3]) // 2))

    def notification_dump():
        return adb("shell", "dumpsys", "notification", "--noredact")

    def wait_notification(predicate, description, timeout=30):
        deadline = time.monotonic() + timeout
        last = ""
        while time.monotonic() < deadline:
            last = notification_dump()
            if predicate(last): return last
            time.sleep(0.5)
        (args.output / "timer-notification-timeout.txt").write_text(last, encoding="utf-8")
        raise TimeoutError(description)

    baseline = None
    if args.baseline_report is not None:
        with args.baseline_report.open("rb") as source:
            encoded = source.read(65537)
        if len(encoded) > 65536:
            raise ValueError("Baseline report is too large.")
        baseline = json.loads(encoded)["scenarios"][0]
        if any(not re.fullmatch(r"[A-F0-9]{64}", baseline.get(key, ""))
               for key in ("identity", "secret_identity")):
            raise ValueError("Baseline report has invalid identity fingerprints.")
    observed_boot = None
    if args.after_boot_id is not None:
        deadline = time.monotonic() + 180
        while time.monotonic() < deadline:
            try:
                observed_boot = uuid.UUID(adb("shell", "cat", "/proc/sys/kernel/random/boot_id").strip())
                if (observed_boot != args.after_boot_id
                        and adb("shell", "getprop", "sys.boot_completed").strip() == "1"):
                    break
            except (subprocess.CalledProcessError, subprocess.TimeoutExpired, ValueError):
                pass  # Re-poll this device; never issue a second reboot on observation failure.
            time.sleep(1)
        else:
            raise TimeoutError("A different completed Android boot was not observed.")
    if adb("shell", "getprop", "sys.boot_completed").strip() != "1":
        raise RuntimeError("Android has not completed booting.")
    # sys.boot_completed can precede package-manager launcher readiness. Observe
    # the same boot/device until resolution succeeds; do not reboot again.
    deadline = time.monotonic() + 30
    while True:
        try:
            lines = adb("shell", "cmd", "package", "resolve-activity", "--brief", PACKAGE).strip().splitlines()
            component = lines[-1] if lines else ""
            if component.startswith(PACKAGE + "/") and re.fullmatch(r"[\w./]+", component):
                break
        except (subprocess.CalledProcessError, subprocess.TimeoutExpired):
            pass
        if time.monotonic() >= deadline:
            raise RuntimeError("The disposable spike launcher could not be resolved.")
        time.sleep(1)
    original_rotation = adb("shell", "wm", "user-rotation").strip().split()
    if original_rotation != ["free"] and not (
            len(original_rotation) == 2 and original_rotation[0] == "lock"
            and original_rotation[1] in ("0", "1", "2", "3")):
        raise RuntimeError("The device's rotation preference could not be read safely.")
    results = []
    try:
        adb("shell", "wm", "user-rotation", "lock", "0")
        if args.onboarding:
            if adb("shell", "pm", "clear", PACKAGE).strip() != "Success":
                raise RuntimeError("The disposable development package could not be cleared.")
            adb("shell", "am", "start", "-W", "-n", component)
            data, root = wait_for(
                lambda r: named(r, "Choose where Snook stores your work") is not None
                and named(r, "Independent workspace") is not None
                and named(r, "Connect to a Snook daemon") is not None,
                "Fresh install did not require an explicit host choice")
            if private_file_exists("no_backup/independent/workspace.db"):
                raise RuntimeError("Fresh launch created a workspace before confirmation.")
            capture("onboarding-unconfigured", data)

            tap(named(root, "Connect to a Snook daemon"))
            data, root = wait_for(
                lambda r: named(r, "Daemon attachment is not enabled in this development build. Snook will not create a local workspace or accept a less-private connection as a fallback.") is not None,
                "Unavailable daemon attachment did not fail closed")
            if private_file_exists("no_backup/independent/workspace.db"):
                raise RuntimeError("Daemon onboarding created an independent workspace fallback.")
            capture("onboarding-attached-fails-closed", data)

            tap(named(root, "Independent workspace"))
            data, root = wait_for(lambda r: named(r, "Create an independent workspace?") is not None,
                                  "Independent setup confirmation did not appear")
            capture("onboarding-independent-confirmation", data)
            adb("shell", "input", "keyevent", "4")
            data, root = wait_for(
                lambda r: named(r, "Independent workspace setup cancelled. No workspace was created.") is not None,
                "Back did not cancel independent setup")
            if private_file_exists("no_backup/independent/workspace.db"):
                raise RuntimeError("Cancelled setup created a workspace.")
            capture("onboarding-independent-cancelled", data)

            tap(named(root, "Independent workspace"))
            _, root = wait_for(lambda r: named(r, "Create an independent workspace?") is not None,
                               "Independent setup confirmation did not reopen")
            tap(next((n for n in root.iter("node")
                      if n.get("text", "").casefold() == "create private workspace"), None))
            data, root = wait_for(lambda r: named(r, "Welcome back") is not None,
                                  "Confirmed independent setup did not open Today")
            if not private_file_exists("no_backup/independent/workspace.db"):
                raise RuntimeError("Confirmed setup did not create the private workspace.")
            capture("onboarding-independent-created", data)

            adb("shell", "am", "force-stop", PACKAGE)
            adb("shell", "am", "start", "-W", "-n", component)
            data, root = wait_for(lambda r: named(r, "Welcome back") is not None,
                                  "Persisted host selection did not reopen after restart")
            if named(root, "Choose where Snook stores your work") is not None:
                raise RuntimeError("Restart forgot the confirmed host selection.")
            capture("onboarding-selection-restarted", data)
            print("PASS: clean-install host choice, no database before confirmation, fail-closed attached entry, cancellation and persisted independent restart. Evidence:", args.output)
            return
        if args.timer_notification:
            if adb("shell", "pm", "clear", PACKAGE).strip() != "Success":
                raise RuntimeError("The disposable development package could not be cleared.")
            adb("shell", "cmd", "statusbar", "collapse")
            size = adb("shell", "wm", "size")
            if "720x1280" not in size:
                raise RuntimeError("The notification action probe is calibrated only for the disposable 720x1280 API 36 emulator.")
            adb("shell", "am", "start", "-W", "-n", component)
            _, root = wait_for(lambda r: named(r, "Independent workspace") is not None,
                               "Fresh install did not show host selection")
            tap(named(root, "Independent workspace"))
            _, root = wait_for(lambda r: named(r, "Create an independent workspace?") is not None,
                               "Independent confirmation did not appear")
            tap(next((n for n in root.iter("node")
                      if n.get("text", "").casefold() == "create private workspace"), None))
            _, root = wait_for(lambda r: named(r, "Welcome back") is not None,
                               "Independent workspace did not open")

            tap(named(root, "More"))
            _, root = wait_for(lambda r: named(r, "Settings") is not None, "More did not open")
            tap(named(root, "Settings"))
            wait_for(lambda r: named(r, "General & data") is not None, "Settings did not open")
            adb("shell", "input", "swipe", "650", "1000", "650", "400", "500")
            data, root = wait_for(lambda r: named(r, "Enable timer notifications") is not None,
                                  "Timer notification education was not reachable")
            capture("timer-permission-education", data)
            tap(named(root, "Enable timer notifications"))
            data, root = wait_for(lambda r: named(r, "Allow") is not None and any(
                "send you notifications" in n.get("text", "") for n in r.iter("node")),
                "Android notification permission did not appear")
            capture("timer-permission-prompt", data)
            tap(named(root, "Allow"))
            _, root = wait_for(lambda r: named(r, "Timer notifications enabled. Running and paused timers will stay synchronized with committed workspace state.") is not None
                               and named(r, "Review Live Update access") is not None,
                               "Notification opt-in did not complete")
            tap(named(root, "Timer"))
            wait_for(lambda r: named(r, "Time Tracker") is not None, "Timer page did not open")
            adb("shell", "input", "swipe", "650", "1000", "650", "420", "500")
            data, root = wait_for(lambda r: named(r, "Admin") is not None and named(r, "Start") is not None,
                                  "The disposable Admin activity was not reachable")
            tap(named(root, "Start"))

            running = wait_notification(lambda value: "pkg=org.snook.spike" in value
                and "channel=active-timers-v1" in value and 'android.title=String (Admin)' in value
                and '[0] "Pause"' in value and '[1] "Stop"' in value
                and "ONGOING_EVENT" in value and "ONLY_ALERT_ONCE" in value and "vis=PRIVATE" in value
                and "vis=PUBLIC" in value and "category=stopwatch" in value
                and "android.requestPromotedOngoing" in value and "Boolean (true)" in value,
                "The committed timer did not produce the expected private ongoing notification")
            files = [name for name in adb("shell", "run-as", PACKAGE, "ls", "no_backup/timer-actions").splitlines()
                     if re.fullmatch(r"[A-F0-9]{64}\.bin", name)]
            if len(files) != 3:
                raise RuntimeError("The running notification did not retain exactly three opaque actions.")
            for name in files:
                size_text = adb("shell", "run-as", PACKAGE, "stat", "-c", "%s", f"no_backup/timer-actions/{name}").strip()
                if size_text != "119": raise RuntimeError("A timer action record was not the expected bounded encrypted envelope.")
            report_lines = [line.strip() for line in running.splitlines()
                            if any(marker in line for marker in ("NotificationRecord(", "channel=active-timers-v1", "flags=", "category=", "actions=2", "vis=", 'android.title=String (Admin)', "android.requestPromotedOngoing", '[0] "Pause"', '[1] "Stop"'))]
            (args.output / "timer-running-notification.txt").write_text("\n".join(report_lines) + "\n", encoding="utf-8")

            app_pid = process_id()
            adb("shell", "input", "keyevent", "3")
            open_notification_shade()
            capture_screen("timer-running-notification")
            adb("shell", "input", "tap", "350", "500")
            data, root = wait_for(lambda r: named(r, "Time Tracker") is not None or named(r, "Time tracker") is not None,
                                  "Notification content did not open the validated timer destination")
            if process_id() != app_pid:
                raise RuntimeError("Content navigation unexpectedly replaced the live app process.")
            capture("timer-content-destination", data)

            adb("shell", "input", "keyevent", "3")
            open_notification_shade()
            adb("shell", "input", "tap", "130", "592")
            paused = wait_notification(lambda value: "pkg=org.snook.spike" in value
                and '[0] "Resume"' in value and 'android.text=String (Background · Paused' in value
                and "android.requestPromotedOngoing" not in value,
                "The notification Pause action did not commit and reconcile")
            (args.output / "timer-paused-notification.txt").write_text("\n".join(
                line.strip() for line in paused.splitlines()
                if any(marker in line for marker in ('[0] "Resume"', '[1] "Stop"', 'android.text=String (Background · Paused'))) + "\n",
                encoding="utf-8")

            adb("shell", "input", "keyevent", "3")
            old_pid = process_id()
            adb("shell", "am", "kill", PACKAGE)
            deadline = time.monotonic() + 10
            while time.monotonic() < deadline and process_id(): time.sleep(0.2)
            if process_id(): raise RuntimeError("The paused timer process was not evicted.")
            if "pkg=org.snook.spike" not in notification_dump() or '[0] "Resume"' not in notification_dump():
                raise RuntimeError("The paused notification did not survive ordinary process eviction.")
            open_notification_shade()
            capture_screen("timer-paused-cold-notification")
            adb("shell", "input", "tap", "130", "592")
            wait_notification(lambda value: "pkg=org.snook.spike" in value and '[0] "Pause"' in value,
                              "The cold-process Resume action did not commit and reconcile")
            new_pid = process_id()
            if not new_pid or new_pid == old_pid:
                raise RuntimeError("The Resume action did not activate a different cold app process.")

            previous_boot = uuid.UUID(adb("shell", "cat", "/proc/sys/kernel/random/boot_id").strip())
            adb("reboot")
            subprocess.run([args.adb, "-s", args.serial, "wait-for-device"], check=True,
                           stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=180)
            boot_deadline = time.monotonic() + 180
            observed_timer_boot = None
            while time.monotonic() < boot_deadline:
                try:
                    candidate = uuid.UUID(adb("shell", "cat", "/proc/sys/kernel/random/boot_id").strip())
                    if candidate != previous_boot and adb("shell", "getprop", "sys.boot_completed").strip() == "1":
                        observed_timer_boot = candidate
                        break
                except (subprocess.CalledProcessError, subprocess.TimeoutExpired, ValueError):
                    pass
                time.sleep(1)
            if observed_timer_boot is None:
                raise TimeoutError("A different completed Android boot was not observed for timer reconciliation.")
            boot_notification = wait_notification(
                lambda value: "pkg=org.snook.spike" in value
                and "channel=active-timers-v1" in value and 'android.title=String (Admin)' in value
                and '[0] "Pause"' in value and '[1] "Stop"' in value
                and "android.requestPromotedOngoing" in value and "Boolean (true)" in value,
                "BOOT_COMPLETED did not reconstruct the committed running timer notification", timeout=90)
            boot_pid = process_id()
            if not boot_pid:
                raise RuntimeError("Timer boot reconciliation did not activate the application process.")
            (args.output / "timer-boot-reconciliation.txt").write_text(
                f"previous_boot_id={previous_boot}\nobserved_boot_id={observed_timer_boot}\n"
                f"pre_boot_process={new_pid}\npost_boot_process={boot_pid}\n"
                + "\n".join(line.strip() for line in boot_notification.splitlines()
                             if any(marker in line for marker in ("NotificationRecord(",
                                 "channel=active-timers-v1", '[0] "Pause"', '[1] "Stop"'))) + "\n",
                encoding="utf-8")
            files = [name for name in adb("shell", "run-as", PACKAGE, "ls", "no_backup/timer-actions").splitlines()
                     if re.fullmatch(r"[A-F0-9]{64}\.bin", name)]
            if len(files) != 3:
                raise RuntimeError("Boot reconciliation did not retain exactly three current opaque actions.")
            open_notification_shade()
            capture_screen("timer-boot-reconciled-notification")
            # A fresh boot places Android's serial-console notice above the timer,
            # so the timer row is collapsed and its action buttons are not yet
            # present at the pre-boot coordinates.
            adb("shell", "input", "tap", "655", "642")
            time.sleep(0.5)
            capture_screen("timer-boot-reconciled-expanded")

            adb("shell", "input", "tap", "240", "742")
            wait_notification(lambda value: "pkg=org.snook.spike" not in value,
                              "The notification Stop action did not remove the timer surface")
            remaining = adb("shell", "run-as", PACKAGE, "ls", "no_backup/timer-actions").splitlines()
            if any(re.fullmatch(r"[A-F0-9]{64}\.(?:bin|tmp)", name) for name in remaining):
                raise RuntimeError("Stopped timer action records were not removed.")
            open_notification_shade()
            capture_screen("timer-stopped-notification-removed")
            print("PASS: timer permission education, committed chronometer, private/public surfaces, validated content destination, pause, cold-process resume, reboot reconstruction, stop and encrypted action cleanup. Evidence:", args.output)
            return
        if review_file:
            adb("shell", "am", "force-stop", PACKAGE)
            adb("shell", "am", "start", "-W", "-n", component)
            _, root = wait_for(lambda r: named(r, "Welcome back") is not None, "Today did not open")
            if named(root, "Dismiss recovery notice") is not None:
                tap(named(root, "Dismiss recovery notice"))
            if args.backup_restore:
                _, root = wait_for(lambda r: named(r, "What needs doing?") is not None, "Quick capture missing")
                tap(named(root, "What needs doing?"))
                adb("shell", "input", "keycombination", "113", "29")
                adb("shell", "input", "text", "DiscardOnRestore")
                wait_for(lambda r: named(r, "DiscardOnRestore") is not None, "Capture draft missing")
                adb("shell", "input", "keyevent", "4")
                time.sleep(1)  # Let the existing bounded capture debounce persist its fixture.
                adb("shell", "run-as", PACKAGE, "ls", "no_backup/independent/capture-draft.bin")
                _, root = tree()
            tap(named(root, "More"))
            _, root = wait_for(lambda r: named(r, "Settings") is not None, "More did not open")
            tap(named(root, "Settings"))
            _, root = wait_for(lambda r: named(r, "Review backup to restore") is not None, "Review action missing")
            tap(named(root, "Review backup to restore"))
            wait_for(lambda r: named(r, "Downloads") is not None, "System open picker missing")
            adb("shell", "input", "keyevent", "4")
            data, root = wait_for(lambda r: named(r, "Backup review cancelled. No workspace data was changed.") is not None, "Picker cancellation was not reported")
            capture("review-picker-cancelled", data)

            def review():
                _, current = wait_for(lambda r: named(r, "Review backup to restore") is not None, "Review action missing")
                tap(named(current, "Review backup to restore"))
                _, current = wait_for(lambda r: named(r, review_file) is not None, "Selected backup is not visible in Downloads")
                tap(named(current, review_file))
                return wait_for(lambda r: named(r, "Verified backup review") is not None, "Verified review did not appear")

            data, root = review()
            message = " ".join(n.get("text", "") for n in root.iter("node"))
            if not all(part in message for part in ("Current:", "Incoming:", "SHA-256", "Unsaved drafts", "does not merge")):
                raise RuntimeError("Review lacks verified identities or explicit destructive warning")
            capture("review-identities", data)
            if args.backup_restore:
                identities = re.findall(r"[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}", message)
                if len(identities) != 2 or identities[0] == identities[1]:
                    raise RuntimeError("Restore acceptance requires a backup from a different disposable workspace")
                button = next((n for n in root.iter("node") if n.get("text", "").casefold() == "replace workspace"), None)
                tap(button)
                data, root = wait_for(lambda r: named(r, "Backup restored and reopened. Previous database retained privately for recovery.") is not None
                                     and named(r, "Welcome back") is not None, "Restore did not reopen the incoming workspace")
                if named(root, "DiscardOnRestore") is not None:
                    raise RuntimeError("Old capture draft survived replacement")
                capture("restore-reopened", data)
                names = adb("shell", "run-as", PACKAGE, "ls", "no_backup/independent").splitlines()
                if "capture-draft.bin" in names or not any(n.startswith("workspace.db.before-restore-") for n in names):
                    raise RuntimeError("Capture cleanup or rollback retention failed")
                adb("shell", "am", "force-stop", PACKAGE)
                copied = args.output / "restored-workspace.db"
                copied.write_bytes(adb("exec-out", "run-as", PACKAGE, "cat", "no_backup/independent/workspace.db", binary=True))
                with sqlite3.connect(f"file:{copied}?mode=ro", uri=True) as connection:
                    if connection.execute("select id from workspaces").fetchone()[0] != identities[1]:
                        raise RuntimeError("Restored database identity differs from the confirmed incoming workspace")
                adb("shell", "am", "start", "-W", "-n", component)
                data, root = wait_for(lambda r: named(r, "Welcome back") is not None, "Restored workspace did not survive restart")
                if named(root, "DiscardOnRestore") is not None or named(root, "Unsaved capture recovered. Review before adding the task.") is not None:
                    raise RuntimeError("Old capture recovered after restored workspace restart")
                capture("restore-restarted", data)
                if any(n.startswith("document-review-") for n in adb("shell", "run-as", PACKAGE, "ls", "no_backup").splitlines()):
                    raise RuntimeError("Restore left document staging behind")
                print("PASS: confirmed different-workspace restore, rollback retention, capture discard, database identity and process restart. Evidence:", args.output)
                return
            adb("shell", "input", "keyevent", "4")
            wait_for(lambda r: named(r, "Backup verified. Restore cancelled; no workspace data was changed.") is not None, "Back did not close review")
            review()
            adb("shell", "wm", "user-rotation", "lock", "1")
            data, root = wait_for(lambda r: named(r, "Backup verified. Restore cancelled; no workspace data was changed.") is not None and named(r, "Verified backup review") is None,
                                  "Rotation did not dismiss the activity-bound review")
            capture("review-rotation-cancelled", data)
            if any(name.startswith("document-review-") for name in adb("shell", "run-as", PACKAGE, "ls", "no_backup").splitlines()):
                raise RuntimeError("Backup review left private staging behind")
            print("PASS: native backup picker cancellation, verified identity review, Back/rotation dismissal and private cleanup. Evidence:", args.output)
            return
        if args.backup_export or args.document_export:
            kind = args.document_export or "Backup"
            action = {"Backup": "Create verified backup", "JSON": "Export workspace JSON", "CSV": "Export CSV worklog"}[kind]
            prefix = kind.lower()
            adb("shell", "am", "force-stop", PACKAGE)
            adb("shell", "am", "start", "-W", "-n", component)
            _, root = wait_for(lambda r: named(r, "Welcome back") is not None, "Today did not open")
            if named(root, "Dismiss recovery notice") is not None:
                tap(named(root, "Dismiss recovery notice"))
            tap(named(root, "More"))
            _, root = wait_for(lambda r: named(r, "Settings") is not None, "More did not open")
            tap(named(root, "Settings"))
            _, root = wait_for(lambda r: named(r, action) is not None, "Settings did not open")
            tap(named(root, action))
            wait_for(lambda r: named(r, "SAVE") is not None, "Save picker did not open")
            adb("shell", "input", "keyevent", "4")
            data, root = wait_for(lambda r: named(r, f"{kind} cancelled. No workspace data was changed.") is not None,
                                  "Picker cancellation was not handled")
            capture(prefix + "-cancelled", data)
            tap(named(root, action))
            data, root = wait_for(lambda r: named(r, "SAVE") is not None and named(r, "Downloads") is not None,
                                  "Disposable Downloads destination not selected")
            capture(prefix + "-picker", data)
            tap(named(root, "SAVE"))
            def completed_export(r):
                return any(n.get("text", "").startswith((f"{kind} exported and read-back verified", f"{kind} export failed")) for n in r.iter("node"))
            data, root = wait_for(completed_export, f"{kind} export did not finish")
            capture(prefix + "-result", data)
            if not any(n.get("text", "").startswith(f"{kind} exported and read-back verified") for n in root.iter("node")):
                raise RuntimeError("Native export failed; inspect type-only SnookExport diagnostics")
            if any(name.startswith(("document-export-", "backup-export-")) for name in adb("shell", "run-as", PACKAGE, "ls", "no_backup").splitlines()):
                raise RuntimeError("Document export left private staging behind")
            print(f"PASS: {kind} picker cancellation, verified document export/read-back and private staging cleanup. Evidence:", args.output)
            return
        if args.editor_back:
            adb("shell", "am", "force-stop", PACKAGE)
            adb("shell", "am", "start", "-W", "-n", component)
            _, root = wait_for(lambda r: named(r, "Welcome back") is not None, "Today did not open")
            if named(root, "Dismiss recovery notice") is not None:
                tap(named(root, "Dismiss recovery notice"))
            tap(named(root, "Timer"))
            _, root = wait_for(lambda r: named(r, "New activity") is not None, "Tracker did not open")
            tap(named(root, "New activity"))
            _, root = wait_for(lambda r: named(r, "e.g. Design exploration") is not None, "Activity editor did not open")
            tap(named(root, "e.g. Design exploration"))
            draft = "BackDraftKeep"
            adb("shell", "input", "text", draft)
            wait_for(lambda r: named(r, draft) is not None, "Draft was not entered")
            adb("shell", "input", "keyevent", "4")
            data, root = wait_for(lambda r: named(r, draft) is not None, "IME Back lost the draft")
            if named(root, "Discard editor changes?") is not None:
                raise RuntimeError("IME Back incorrectly requested editor discard")
            capture("back-ime-retained", data)

            def confirmation():
                adb("shell", "input", "keyevent", "4")
                return wait_for(lambda r: named(r, "Discard editor changes?") is not None,
                                "System Back did not request confirmation")

            def dialog_button(root, label):
                return next((n for n in root.iter("node") if n.get("text", "").casefold() == label.casefold()), None)

            data, root = confirmation()
            capture("back-confirmation", data)
            tap(dialog_button(root, "Keep editing"))
            data, root = wait_for(lambda r: named(r, draft) is not None, "Keep editing lost the draft")
            capture("back-kept", data)
            confirmation()
            adb("shell", "input", "keyevent", "4")
            wait_for(lambda r: named(r, draft) is not None, "Back on confirmation discarded the draft")
            confirmation()
            pid = adb("shell", "pidof", PACKAGE).strip()
            adb("shell", "wm", "user-rotation", "lock", "1")
            data, root = wait_for(lambda r: r.get("rotation") == "1" and named(r, draft) is not None,
                                  "Rotation of confirmation lost the draft")
            if adb("shell", "pidof", PACKAGE).strip() != pid:
                raise RuntimeError("Rotation restarted the process")
            capture("back-rotation-retained", data)
            adb("shell", "wm", "user-rotation", "lock", "0")
            wait_for(lambda r: r.get("rotation") == "0" and named(r, draft) is not None, "Portrait editor did not return")
            _, root = confirmation()
            tap(dialog_button(root, "Discard changes"))
            data, root = wait_for(lambda r: named(r, "New activity") is not None and named(r, "Cancel editor") is None,
                                  "Confirmed discard did not close the editor")
            capture("back-discarded", data)
            tap(named(root, "New activity"))
            data, root = wait_for(lambda r: named(r, "e.g. Design exploration") is not None, "Editor did not reopen")
            if named(root, draft) is not None:
                raise RuntimeError("Discarded draft returned")
            capture("back-reopened-empty", data)
            tap(named(root, "Cancel editor"))
            wait_for(lambda r: named(r, "Cancel editor") is None, "Editor did not cancel")
            adb("shell", "input", "keyevent", "4")
            data, _ = wait_for(lambda r: not any(n.get("package") == PACKAGE for n in r.iter("node")),
                               "Root Back trapped the user in the app")
            capture("back-root-exit", data)
            print("PASS: IME Back, keep editing, dialog Back, rotation retention, explicit discard, empty reopen and root exit. Evidence:", args.output)
            return
        for name in ("cold-start", "process-restart"):
            adb("shell", "am", "force-stop", PACKAGE)
            adb("shell", "am", "start", "-W", "-n", component)
            data, root = wait_for(lambda r: named(r, "Snook · On this device") is not None
                                  and any(named(r, heading) is not None for heading in
                                          ("Welcome back", "Your tasks", "Your daily habits",
                                           "A little space to reflect", "Time tracker",
                                           "Make room for the work", "Time history",
                                           "Time summary", "Workspace settings")),
                                  "Normal launch did not open the saved workspace destination")
            if named(root, "Welcome back") is None:
                tap(named(root, "Today"))
                data, root = wait_for(lambda r: named(r, "Welcome back") is not None,
                                      "Today could not be selected for the baseline probe")
            if named(root, "Discard share") is not None:
                raise RuntimeError("Normal launch was incorrectly treated as a share.")
            capture(name + "-workspace", data)
            if args.capture_recovery and named(root, "AndroidRotationDraft") is not None:
                tap(named(root, "AndroidRotationDraft"))
                adb("shell", "input", "keyevent", "123", *(["67"] * len("AndroidRotationDraft")))
                adb("shell", "input", "keyevent", "111")
                time.sleep(1)
                data, root = wait_for(lambda r: named(r, "AndroidRotationDraft") is None, "Prior test capture could not be cleared")
            if named(root, "Dismiss recovery notice") is not None:
                tap(named(root, "Dismiss recovery notice"))
                data, root = wait_for(lambda r: named(r, "Dismiss recovery notice") is None, "Recovery notice did not dismiss")
            tap(named(root, "Tools"))
            _, root = wait_for(lambda r: named(r, "Platform checks") is not None, "Tools did not open")
            tap(named(root, "Platform checks"))
            _, root = wait_for(lambda r: r.get("rotation") == "0" and result(r) == "Checks have not run.",
                               "Fresh activity at rotation 0 did not appear")
            button = next(n for n in root.iter("node") if n.get("resource-id") == "RunButton")
            if button.get("enabled") != "true":
                raise RuntimeError("The run button is disabled.")
            bounds = [int(v) for v in re.findall(r"\d+", button.get("bounds", ""))]
            if len(bounds) != 4 or bounds[2] <= bounds[0] or bounds[3] <= bounds[1]:
                raise RuntimeError("The run button is not reachable.")
            adb("shell", "input", "tap", str((bounds[0] + bounds[2]) // 2), str((bounds[1] + bounds[3]) // 2))
            data, root = wait_for(lambda r: result(r).startswith("PASS:"), "Platform checks did not pass")
            capture(name, data)
            fingerprint = re.search(r"Identity fingerprint: ([A-F0-9]{64})\.", result(root))
            if fingerprint is None:
                raise RuntimeError("The probe did not report its persisted identity fingerprint.")
            if results and results[0]["identity"] != fingerprint[1]:
                raise RuntimeError("Process restart changed the persisted workspace or receipt identity.")
            secret_fingerprint = re.search(r"Secret fingerprint: ([A-F0-9]{64})\.", result(root))
            if secret_fingerprint is None:
                raise RuntimeError("The probe did not report its Keystore secret fingerprint.")
            if results and results[0]["secret_identity"] != secret_fingerprint[1]:
                raise RuntimeError("Process restart changed the protected secret.")
            if baseline is not None and (baseline["identity"] != fingerprint[1]
                                         or baseline["secret_identity"] != secret_fingerprint[1]):
                raise RuntimeError("The workspace or protected secret differs from the prior report.")
            results.append({"scenario": name, "result": result(root), "identity": fingerprint[1],
                            "secret_identity": secret_fingerprint[1]})

        adb("shell", "wm", "user-rotation", "lock", "1")
        data, root = wait_for(lambda r: r.get("rotation") == "1" and result(r) == results[-1]["result"],
                              "Rotation did not retain the process-owned probe result")
        capture("landscape", data)
        results.append({"scenario": "landscape", "result": result(root),
                        "identity": results[-1]["identity"], "secret_identity": results[-1]["secret_identity"]})
        if args.workspace:
            tap(named(root, "Open independent workspace"))
            # Avalonia Android currently exposes this input's placeholder as the
            # content description, not its explicit desktop automation name.
            data, root = wait_for(lambda r: named(r, "What needs doing?") is not None,
                                  "The shared workspace did not open")
            capture("workspace-landscape", data)
            tap(named(root, "What needs doing?"))
            draft = "AndroidRotationDraft"
            adb("shell", "input", "text", draft)
            data, root = wait_for(lambda r: any(n.get("text") == draft for n in r.iter("node")),
                                  "The workspace capture draft was not entered")
            capture("workspace-draft", data)
            adb("shell", "input", "keyevent", "111")  # Escape dismisses the IME, not Android Back.
            pid = adb("shell", "pidof", PACKAGE).strip()
            adb("shell", "wm", "user-rotation", "lock", "0")
            data, root = wait_for(lambda r: r.get("rotation") == "0"
                                  and any(n.get("text") == draft for n in r.iter("node")),
                                  "Activity recreation lost the workspace capture draft")
            if not pid or adb("shell", "pidof", PACKAGE).strip() != pid:
                raise RuntimeError("Process identity changed during the activity-only check.")
            capture("workspace-rotated-draft", data)
            tap(named(root, "More"))
            data, root = wait_for(lambda r: named(r, "Habits") is not None,
                                  "Compact More navigation did not open")
            capture("workspace-more", data)
            tap(named(root, "Habits"))
            data, root = wait_for(lambda r: named(r, "Your daily habits") is not None,
                                  "More navigation did not select Habits")
            capture("workspace-more-destination", data)
            tap(named(root, "Today"))
            data, root = wait_for(lambda r: any(n.get("text") == draft for n in r.iter("node")),
                                  "Navigation lost the unsaved capture draft")
            capture("workspace-returned-draft", data)
            tap(named(root, "Tasks"))
            data, root = wait_for(lambda r: any(n.get("resource-id") == "TaskCaptureTitle"
                                               and n.get("text") == draft for n in r.iter("node")),
                                  "Tasks did not retain the shared capture draft")
            title_node = next(n for n in root.iter("node") if n.get("resource-id") == "TaskCaptureTitle")
            project_node = next(n for n in root.iter("node") if n.get("resource-id") == "TaskCaptureProject")
            title_bounds = [int(v) for v in re.findall(r"\d+", title_node.get("bounds", ""))]
            project_bounds = [int(v) for v in re.findall(r"\d+", project_node.get("bounds", ""))]
            if len(title_bounds) != 4 or len(project_bounds) != 4 or project_bounds[1] < title_bounds[3]:
                raise RuntimeError("Compact Tasks project picker did not move below the title.")
            capture("workspace-compact-tasks", data)
            foreground_pid = adb("shell", "pidof", PACKAGE).strip()
            adb("shell", "input", "keyevent", "3")  # Home: stop visibility without killing the process.
            data, root = wait_for(lambda r: not any(n.get("package") == PACKAGE for n in r.iter("node")),
                                  "The workspace did not leave the foreground")
            capture("workspace-background", data)
            adb("shell", "am", "start", "-W", "-n", component)
            data, root = wait_for(lambda r: any(n.get("resource-id") == "TaskCaptureTitle"
                                               and n.get("text") == draft for n in r.iter("node")),
                                  "Foreground return lost the workspace capture draft")
            if not foreground_pid or adb("shell", "pidof", PACKAGE).strip() != foreground_pid:
                raise RuntimeError("Process changed during the visibility-only check.")
            capture("workspace-foreground-return", data)
            results.append({"scenario": "workspace-rotation", "result": "Capture draft retained in the same process; no save requested."})
            results.append({"scenario": "compact-navigation", "result": "More selects Habits; Today returns with the capture draft retained."})
            results.append({"scenario": "compact-tasks", "result": "Tasks retains capture and places its project picker below the title."})
            results.append({"scenario": "foreground-return", "result": "Home hides the workspace; returning in the same process retains capture."})
            if args.capture_recovery:
                encrypted = adb("exec-out", "run-as", PACKAGE, "cat", "no_backup/independent/capture-draft.bin", binary=True)
                if not 30 <= len(encrypted) <= 4125 or draft.encode() in encrypted:
                    raise RuntimeError("Capture recovery file is absent, oversized, or contains plaintext draft text.")
                navigation = adb("exec-out", "run-as", PACKAGE, "cat",
                                 "no_backup/independent/navigation-state.json", binary=True)
                if not 1 <= len(navigation) <= 2048 or draft.encode() in navigation:
                    raise RuntimeError("Navigation state is absent, oversized, or contains capture text.")
                navigation_state = json.loads(navigation)
                if navigation_state.get("Section") != "Tasks":
                    raise RuntimeError("Navigation state did not checkpoint the Tasks destination.")
                old_pid = adb("shell", "pidof", PACKAGE).strip()
                adb("shell", "am", "force-stop", PACKAGE)
                adb("shell", "am", "start", "-W", "-n", component)
                data, root = wait_for(lambda r: named(r, "Unsaved capture recovered. Review before adding the task.") is not None
                                      and any(n.get("resource-id") == "TaskCaptureTitle" and n.get("text") == draft
                                              for n in r.iter("node")),
                                      "Process restart did not recover the Tasks destination and encrypted capture")
                if adb("shell", "pidof", PACKAGE).strip() == old_pid:
                    raise RuntimeError("The recovery check did not replace the app process.")
                capture("capture-process-recovery", data)
                tap(named(root, "Dismiss recovery notice"))
                data, root = wait_for(lambda r: any(n.get("text") == draft for n in r.iter("node")), "Recovered capture is unavailable")
                tap(next(n for n in root.iter("node") if n.get("text") == draft))
                adb("shell", "input", "keyevent", "123", *(["67"] * len(draft)))
                adb("shell", "input", "keyevent", "111")
                deadline = time.monotonic() + 30
                while time.monotonic() < deadline:
                    exists = adb("shell", "run-as", PACKAGE, "sh", "-c",
                                 "'if test -f no_backup/independent/capture-draft.bin; then echo present; else echo absent; fi'").strip()
                    if exists == "absent": break
                    time.sleep(0.2)
                else: raise RuntimeError("Clearing capture did not remove its recovery file.")
                adb("shell", "am", "force-stop", PACKAGE)
                adb("shell", "am", "start", "-W", "-n", component)
                data, root = wait_for(lambda r: any(n.get("resource-id") == "TaskCaptureTitle"
                                                     for n in r.iter("node")),
                                      "Restart after capture discard did not restore Tasks")
                if any(n.get("text") == draft for n in root.iter("node")):
                    raise RuntimeError("Cleared capture reappeared after restart.")
                capture("capture-cleared-restart", data)
                results.append({"scenario": "capture-recovery", "result": "Bounded encrypted file, different-process restoration, and cleared-capture restart verified without saving a task."})
                results.append({"scenario": "navigation-recovery", "result": "Bounded non-secret state restored Tasks across two process restarts without storing capture text."})
                saved_title = "AndroidEditorRecoveryTask"
                tap(next(n for n in root.iter("node") if n.get("resource-id") == "TaskCaptureTitle"))
                adb("shell", "input", "text", saved_title)
                adb("shell", "input", "keyevent", "111")
                data, root = wait_for(lambda r: named(r, saved_title) is not None,
                                      "Task title was not entered for editor recovery")
                tap(named(root, "Create task from Tasks"))
                data, root = wait_for(lambda r: named(r, saved_title) is not None
                                      and any(n.get("resource-id") == "TaskCaptureTitle" and not n.get("text")
                                              for n in r.iter("node")),
                                      "Editor recovery fixture task was not saved")
                adb("shell", "input", "swipe", "650", "1040", "650", "650", "500")
                data, root = wait_for(
                    lambda r: any((n.get("text") == "Edit task" or n.get("content-desc") == "Edit task")
                                  and (lambda b: len(b) == 4 and b[3] < 1158)
                                  ([int(v) for v in re.findall(r"\d+", n.get("bounds", ""))])
                                  for n in r.iter("node")),
                    "Saved task Edit action did not scroll above compact navigation")
                task_card = next((n for n in root.iter("node")
                                  if any(child.get("text") == saved_title for child in n.iter("node"))
                                  and any(child.get("text") == "Edit task" or child.get("content-desc") == "Edit task"
                                          for child in n.iter("node"))), None)
                if task_card is None:
                    raise RuntimeError("Saved editor recovery fixture has no reachable Edit action.")
                tap(next((n for n in task_card.iter("node")
                          if n.get("text") == "Edit task" or n.get("content-desc") == "Edit task"), None))
                data, root = wait_for(lambda r: any(n.get("resource-id") == "TaskEditorTitle"
                                                     for n in r.iter("node")),
                                      "Task editor did not open")
                editor_draft = "AndroidEditorRecoveredDraft"
                tap(next(n for n in root.iter("node") if n.get("resource-id") == "TaskEditorTitle"))
                adb("shell", "input", "keyevent", "123", *(["67"] * len(saved_title)))
                adb("shell", "input", "text", editor_draft)
                adb("shell", "input", "keyevent", "4")  # IME consumes Back before editor navigation.
                data, root = wait_for(lambda r: named(r, editor_draft) is not None,
                                      "Task editor draft was not entered")
                time.sleep(1)
                editor_encrypted = adb("exec-out", "run-as", PACKAGE, "cat",
                                       "no_backup/independent/task-editor-draft.bin", binary=True)
                if not 30 <= len(editor_encrypted) <= 32832 or editor_draft.encode() in editor_encrypted:
                    raise RuntimeError("Task-editor recovery file is absent, oversized, or contains plaintext draft text.")
                editor_pid = adb("shell", "pidof", PACKAGE).strip()
                adb("shell", "am", "force-stop", PACKAGE)
                adb("shell", "am", "start", "-W", "-n", component)
                data, root = wait_for(lambda r: named(r, "Unsaved task edit recovered. Review it before saving.") is not None
                                      and named(r, editor_draft) is not None,
                                      "Process restart did not recover the encrypted task editor")
                if adb("shell", "pidof", PACKAGE).strip() == editor_pid:
                    raise RuntimeError("The task-editor recovery check did not replace the app process.")
                capture("task-editor-process-recovery", data)
                tap(named(root, "Cancel task editing"))
                wait_for(lambda r: named(r, editor_draft) is None, "Cancel did not close the recovered task editor")
                deadline = time.monotonic() + 30
                while time.monotonic() < deadline:
                    if not private_file_exists("no_backup/independent/task-editor-draft.bin"): break
                    time.sleep(0.2)
                else: raise RuntimeError("Cancel did not remove the task-editor recovery file.")
                data, root = tree()
                if named(root, "Dismiss recovery notice") is not None:
                    tap(named(root, "Dismiss recovery notice"))
                    wait_for(lambda r: named(r, "Dismiss recovery notice") is None,
                             "Task-editor recovery notice did not dismiss")
                adb("shell", "am", "force-stop", PACKAGE)
                adb("shell", "am", "start", "-W", "-n", component)
                data, root = wait_for(lambda r: any(n.get("resource-id") == "TaskCaptureTitle"
                                                     for n in r.iter("node")),
                                      "Restart after task-editor Cancel did not restore Tasks")
                if named(root, editor_draft) is not None:
                    raise RuntimeError("Cancelled task-editor draft reappeared after restart.")
                data, root = wait_for(lambda r: named(r, saved_title) is not None,
                                      "Cancel or restart changed the persisted task")
                if named(root, editor_draft) is not None:
                    raise RuntimeError("Cancelled task-editor draft replaced the persisted task.")
                capture("task-editor-cleared-restart", data)
                results.append({"scenario": "task-editor-recovery", "result": "Bounded encrypted task editor restored in a different process, never auto-saved, and stayed cleared after Cancel and restart."})
            if args.share:
                shared = "AndroidSharedCapture"
                adb("shell", "am", "start", "-W", "-n", component, "-a", "android.intent.action.SEND",
                    "-t", "text/plain", "--es", "android.intent.extra.TEXT", shared)
                data, root = wait_for(lambda r: named(r, "Review shared task") is not None,
                                      "Warm share did not offer review")
                tap(named(root, "Review shared task"))
                data, root = wait_for(lambda r: named(r, "Finish or discard the current draft before reviewing shared text.") is not None,
                                      "Sharing did not protect the existing draft")
                if not any(n.get("text") == draft for n in root.iter("node")):
                    raise RuntimeError("Share replaced the existing capture.")
                capture("share-existing-draft", data)
                tap(named(root, "Discard share"))
                data, root = wait_for(lambda r: any(n.get("text") == draft for n in r.iter("node")), "The existing capture is unavailable")
                tap(next(n for n in root.iter("node") if n.get("text") == draft))
                adb("shell", "input", "keyevent", "123", *(["67"] * len(draft)))
                adb("shell", "input", "keyevent", "111")
                time.sleep(1)  # Allow the debounced clear checkpoint; restart below verifies it.
                adb("shell", "am", "force-stop", PACKAGE)
                adb("shell", "am", "start", "-W", "-n", component, "-a", "android.intent.action.SEND",
                    "-t", "text/plain", "--es", "android.intent.extra.TEXT", shared)
                data, root = wait_for(lambda r: named(r, "Review shared task") is not None,
                                      "Cold share did not offer review")
                capture("share-cold-review", data)
                tap(named(root, "Review shared task"))
                data, root = wait_for(lambda r: any(n.get("resource-id") == "TaskCaptureTitle" and n.get("text") == shared
                                                   for n in r.iter("node")), "Share did not prefill an unsaved capture")
                capture("share-prefilled", data)
                adb("shell", "wm", "user-rotation", "lock", "1")
                data, root = wait_for(lambda r: r.get("rotation") == "1" and any(n.get("text") == shared for n in r.iter("node")),
                                      "Rotation lost the shared draft")
                if named(root, "Review shared task") is not None:
                    raise RuntimeError("Rotation redelivered the consumed share.")
                capture("share-rotated", data)

                adb("shell", "am", "start", "-W", "-n", component, "-a", "android.intent.action.SEND",
                    "-t", "text/plain", "--es", "android.intent.extra.TEXT", "SubstitutionAttempt",
                    "--es", "unexpected", "value")
                data, root = wait_for(lambda r: named(r, "Share not accepted. Use plain text up to 300 characters and resolve any pending share first.") is not None,
                                      "Share with an unexpected extra was not rejected")
                if not any(n.get("text") == shared for n in root.iter("node")):
                    raise RuntimeError("Rejected share extras changed the draft.")
                capture("share-extra-rejected", data)

                adb("shell", "am", "start", "-W", "-n", component, "-a", "android.intent.action.VIEW",
                    "-d", "file:///untrusted")
                data, root = wait_for(lambda r: named(r, "Entry not accepted. Open Snook normally or share plain text up to 300 characters.") is not None,
                                      "Unsupported URI entry was not rejected")
                if not any(n.get("text") == shared for n in root.iter("node")):
                    raise RuntimeError("Unsupported URI entry changed the draft.")
                capture("view-uri-rejected", data)

                adb("shell", "am", "start", "-W", "-n", component, "-a", "android.intent.action.SEND",
                    "-t", "text/plain", "--es", "android.intent.extra.TEXT", "SubstitutionAttempt",
                    "--es", "unexpected", "value")
                wait_for(lambda r: named(r, "Share not accepted. Use plain text up to 300 characters and resolve any pending share first.") is not None,
                         "Second invalid share did not pass through the entry router")
                forged_token = "A" * 64
                adb("shell", "am", "start", "-W", "-n", component,
                    "-a", "org.snook.spike.OPEN_TIMER", "-d", f"snook-internal://timer/{forged_token}",
                    "--es", "action_token", forged_token, "--es", "unexpected", "value")
                data, root = wait_for(lambda r: named(r, "Entry not accepted. Open Snook normally or share plain text up to 300 characters.") is not None,
                                      "Timer-open substitution was not rejected")
                if not any(n.get("text") == shared for n in root.iter("node")):
                    raise RuntimeError("Rejected timer-open substitution changed the draft.")
                capture("timer-open-extra-rejected", data)
                results.append({"scenario": "entry-router", "result": "Cold/warm text entry requires review and protects drafts; unexpected share extras, a VIEW URI and a timer-open substitution all fail closed without navigation or saving."})
        report = {
            "package": PACKAGE,
            "api": adb("shell", "getprop", "ro.build.version.sdk").strip(),
            "abi": adb("shell", "getprop", "ro.product.cpu.abi").strip(),
            "scenarios": results,
            "previous_boot_id": str(args.after_boot_id) if args.after_boot_id is not None else None,
            "observed_boot_id": str(observed_boot) if observed_boot is not None else None,
            "baseline_matched": baseline is not None,
            "limits": "No claim of physical-device, OS-eviction, full-contract, complete native-integration, or accessibility acceptance. Reboot persistence requires differing boot IDs and a matched baseline in this report.",
        }
        (args.output / "report.json").write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
        print("PASS: Android probe cold start, process restart, and rotation. Evidence:", args.output)
    finally:
        adb("shell", "wm", "user-rotation", *original_rotation)


if __name__ == "__main__":
    main()
