#!/usr/bin/env python3
"""Verify cached RSS state after an offline process restart on a disposable emulator.

Requires a Debug org.snook.spike independent workspace with a refreshed RSS feed,
at least one article marked read and starred, and RSS as the current destination.
Never clears app data. Temporarily disables emulator Wi-Fi/mobile data and restores
both settings in finally. Refuses physical devices. The output directory must be new.
"""
import argparse
import json
from pathlib import Path
import re
import sqlite3
import subprocess
import time
import xml.etree.ElementTree as ET

PACKAGE = "org.snook.spike"
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("--serial", required=True)
parser.add_argument("--adb", default="adb")
parser.add_argument("--output", required=True, type=Path)
args = parser.parse_args()


def adb(*command, binary=False):
    return subprocess.check_output([args.adb, "-s", args.serial, *command],
                                   text=not binary, timeout=30)


def tree():
    for _ in range(5):
        result = adb("shell", "uiautomator", "dump", "/sdcard/snook-rss-check.xml")
        if "dumped" in result:
            xml = adb("shell", "cat", "/sdcard/snook-rss-check.xml")
            return xml, ET.fromstring(xml)
        time.sleep(1)
    raise RuntimeError("Android UI hierarchy unavailable")


def named(root, text):
    return next((node for node in root.iter("node")
                 if text in (node.get("text"), node.get("content-desc"))), None)


def wait_for(predicate):
    deadline = time.monotonic() + 35
    while time.monotonic() < deadline:
        xml, root = tree()
        if predicate(root):
            return xml, root
        time.sleep(0.5)
    raise RuntimeError("RSS UI did not reach the expected state")


def tap(node):
    if node is None:
        raise RuntimeError("Required RSS control missing")
    x1, y1, x2, y2 = map(int, re.findall(r"\d+", node.get("bounds")))
    adb("shell", "input", "tap", str((x1 + x2) // 2), str((y1 + y2) // 2))


def snapshot(label):
    folder = args.output / label
    folder.mkdir()
    for suffix in ("", "-wal", "-shm"):
        source = "no_backup/independent/workspace.db" + suffix
        exists = adb("shell", "run-as", PACKAGE, "sh", "-c", f"'test -f {source} && echo yes || echo no'").strip()
        if exists == "yes":
            (folder / ("workspace.db" + suffix)).write_bytes(
                adb("exec-out", "run-as", PACKAGE, "cat", source, binary=True))
    with sqlite3.connect(folder / "workspace.db") as connection:
        feeds = [json.loads(row[0]) for row in connection.execute("SELECT payload FROM rss_feeds ORDER BY id")]
        articles = [json.loads(row[0]) for row in connection.execute("SELECT payload FROM rss_articles ORDER BY id")]
    return {"feeds": feeds, "articles": articles}


if adb("shell", "getprop", "ro.kernel.qemu").strip() != "1":
    raise RuntimeError("Refusing to test a physical device")
args.output.mkdir(parents=True, exist_ok=False)
component = adb("shell", "cmd", "package", "resolve-activity", "--brief", PACKAGE).strip().splitlines()[-1]
if not component.startswith(PACKAGE + "/") or not re.fullmatch(r"[\w./]+", component):
    raise RuntimeError("Cannot resolve the disposable app launcher")
wifi = adb("shell", "settings", "get", "global", "wifi_on").strip() == "1"
mobile = adb("shell", "settings", "get", "global", "mobile_data").strip() == "1"
try:
    # RSS navigation metadata is debounced; let the settled destination checkpoint.
    time.sleep(1)
    adb("shell", "am", "force-stop", PACKAGE)
    before = snapshot("before")
    if not before["feeds"] or not any(item["IsRead"] and item["IsStarred"] for item in before["articles"]):
        raise RuntimeError("Prepare a feed and mark an article read and starred before running")
    adb("shell", "svc", "wifi", "disable")
    adb("shell", "svc", "data", "disable")
    adb("shell", "am", "start", "-W", "-n", component)
    xml, root = wait_for(lambda root: named(root, "Your reading room") is not None)
    if named(root, "Dismiss recovery notice") is not None:
        tap(named(root, "Dismiss recovery notice"))
        xml, root = tree()
    tap(named(root, "Starred only"))
    _, root = tree()
    tap(named(root, "Apply filters"))
    _, root = wait_for(lambda root: any("Read · Starred" in node.get("text", "") for node in root.iter("node")))
    # Use the actual screen dimensions; scrolling keeps the cached reader reachable.
    dimensions = adb("shell", "wm", "size")
    width, height = map(int, re.findall(r"(\d+)x(\d+)", dimensions)[-1])
    adb("shell", "input", "swipe", str(width // 2), str(height * 78 // 100),
        str(width // 2), str(height * 37 // 100), "350")
    _, root = tree()
    tap(named(root, "Read cached article"))
    xml, root = wait_for(lambda root: any(node.get("class") == "SelectableTextBlock"
                                         and node.get("text") for node in root.iter("node")))
    (args.output / "offline-reader.xml").write_text(xml)
    (args.output / "offline-reader.png").write_bytes(adb("exec-out", "screencap", "-p", binary=True))
    adb("shell", "am", "force-stop", PACKAGE)
    after = snapshot("after")
    if before != after:
        raise RuntimeError("Offline reading changed persisted RSS data")
    report = {"serial": args.serial, "emulatorOnly": True, "offlineRestart": True,
              "navigationRestored": "RSS", "cachedContentVisible": True,
              "readStarStateRetained": True, "feeds": len(after["feeds"]),
              "articles": len(after["articles"])}
    (args.output / "report.json").write_text(json.dumps(report, indent=2) + "\n")
    print(json.dumps(report, indent=2))
finally:
    adb("shell", "svc", "wifi", "enable" if wifi else "disable")
    adb("shell", "svc", "data", "enable" if mobile else "disable")
    adb("shell", "am", "start", "-W", "-n", component)
