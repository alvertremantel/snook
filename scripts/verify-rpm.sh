#!/usr/bin/env bash
# Inspect and smoke-test a trusted RPM built by package-rpm.sh, without installing it.
set -euo pipefail
if [[ $# -ne 1 ]]; then
  echo "Usage: $0 PATH_TO_TRUSTED_SNOOK_RPM" >&2
  exit 2
fi
ROOT=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
rpm_file=$(realpath -- "$1")
for tool in rpm rpm2cpio cpio python3; do
  command -v "$tool" >/dev/null
done
[[ $(rpm -qp --qf '%{NAME} %{ARCH}' "$rpm_file") == 'snook x86_64' ]]
verify_dir=$(mktemp -d /tmp/snook-rpm-check.XXXXXXXX)
echo "Disposable package verification directory: $verify_dir"
# Only extract locally built/trusted RPMs. This is not an untrusted archive scanner.
# Drain archive padding after cpio's end marker, so pipefail does not mistake a
# successful extraction for rpm2cpio's timing-dependent SIGPIPE.
rpm2cpio "$rpm_file" | (cd "$verify_dir" && cpio -idm --quiet --no-absolute-filenames && cat > /dev/null)
for mapping in 'snook Desktop/Snook' 'snook-cli Cli/snook' 'snookd Daemon/snookd'; do
  read -r launcher payload <<< "$mapping"
  expected=$(printf '#!/usr/bin/sh\nexec /usr/lib64/snook/%s "$@"' "$payload")
  [[ $(<"$verify_dir/usr/bin/$launcher") == "$expected" ]]
  [[ -x "$verify_dir/usr/bin/$launcher" && -x "$verify_dir/usr/lib64/snook/$payload" ]]
done
grep -qx 'Exec=snook' "$verify_dir/usr/share/applications/com.snook.Snook.desktop"
grep -qx 'Terminal=false' "$verify_dir/usr/share/applications/com.snook.Snook.desktop"
cmp "$ROOT/packaging/rpm/com.snook.Snook.desktop" "$verify_dir/usr/share/applications/com.snook.Snook.desktop"
cmp "$ROOT/deploy/systemd/snookd.service" "$verify_dir/usr/lib/systemd/user/snookd.service"
for document in README.md docs/operations.md docs/cli.md docs/json-export.md; do
  [[ -s "$verify_dir/usr/share/doc/snook/$document" ]]
  cmp "$ROOT/$document" "$verify_dir/usr/share/doc/snook/$document"
done
[[ -z $(rpm -qp --scripts "$rpm_file") ]]
requires=$(rpm -qp --requires "$rpm_file")
for dependency in 'glibc(x86-64)' 'libgcc(x86-64)' 'libstdc++(x86-64)' \
  'openssl-libs(x86-64)' 'libicu(x86-64)' 'krb5-libs(x86-64)' ca-certificates tzdata systemd; do
  grep -qxF "$dependency" <<< "$requires"
done
provides=$(rpm -qp --provides "$rpm_file")
if grep -qE '^lib.*[.]so' <<< "$provides" || grep -qE '^libmscordaccore[.]so' <<< "$requires"; then
  echo "Private runtime libraries leaked into RPM dependency metadata." >&2
  exit 1
fi

# Exercise the exact payloads targeted by the verified absolute installed wrappers.
# No chroot/install or real workspace/service is involved.
python3 - "$verify_dir" <<'PY'
import json
import os
import queue
import signal
import socket
import subprocess
import sys
import threading
import uuid
import hashlib

root = sys.argv[1]
payload = root + '/usr/lib64/snook/'
data = root + '/data'
env = {key: value for key, value in os.environ.items() if not key.startswith('SNOOK_')}

def run(executable, *args, expected=0):
    result = subprocess.run([payload + executable, *args], env=env,
                            capture_output=True, text=True, timeout=35)
    if result.returncode != expected:
        raise RuntimeError(f'{executable} exited {result.returncode}: {result.stderr}')
    return result.stdout

def lines(process):
    output = queue.Queue()
    def consume():
        for line in process.stdout:
            output.put(line)
        output.put(None)
    threading.Thread(target=consume, daemon=True).start()
    return output

def record(output):
    line = output.get(timeout=35)
    if line is None:
        raise RuntimeError('Process exited before the expected output')
    return json.loads(line)

assert '--host embedded|daemon' in run('Desktop/Snook', '--help')
assert 'snook-cli' in subprocess.run([payload + 'Cli/snook', 'help'], env=env,
    capture_output=True, text=True, timeout=10, check=True).stderr
assert '--port' in run('Daemon/snookd', '--help')
with socket.socket() as probe:
    probe.bind(('127.0.0.1', 0))
    port = probe.getsockname()[1]

daemon = subprocess.Popen([payload + 'Daemon/snookd', '--data-dir', data,
    '--port', str(port)], env=env, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
watch = None
try:
    assert record(lines(daemon))['ready'] is True
    options = ('--host', 'daemon', '--data-dir', data)
    doctor = json.loads(run('Cli/snook', *options, 'doctor'))
    assert doctor['ready'] is True and doctor['host'] == 'daemon-client'
    watch = subprocess.Popen([payload + 'Cli/snook', *options, 'watch'], env=env,
        stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
    changes = lines(watch)
    # The stream may connect before or after watch's ready record. Require both.
    ready = connected = False
    while not (ready and connected):
        event = record(changes)
        ready |= event['type'] == 'ready'
        connected |= event['type'] == 'change' and event['change']['changeKind'] == 'reconnected'
    create_args = json.dumps({'name': 'RPM smoke', 'request': {
        'operationId': str(uuid.uuid4()), 'clientDeviceId': str(uuid.uuid4())}})
    board = json.loads(run('Cli/snook', *options, 'call', 'create-board', create_args))
    assert json.loads(run('Cli/snook', *options, 'call', 'create-board', create_args)) == board
    while True:
        event = record(changes)
        if event['type'] == 'change' and event['change']['aggregateId'] == board['id']:
            assert event['change']['changeKind'] == 'created'
            break
    snapshot = json.loads(run('Cli/snook', *options, 'bootstrap'))
    assert any(item['id'] == board['id'] for item in snapshot['boards'])
    export_path = root + '/export.json'
    export_args = json.dumps({'destinationPath': export_path})
    exported = json.loads(run('Cli/snook', *options, 'call', 'export-json', export_args))
    assert exported['schemaVersion'] == 5
    with open(export_path, 'rb') as stream:
        exported_bytes = stream.read()
    document = json.loads(exported_bytes)
    assert document['schemaVersion'] == 5
    assert all(key in document for key in ('settings', 'committedCursor', 'taskLinks', 'taskDependencies'))
    assert hashlib.sha256(exported_bytes).hexdigest() == exported['sha256']
    run('Cli/snook', *options, 'call', 'export-json', export_args, expected=2)
    with open(export_path, 'rb') as stream:
        assert stream.read() == exported_bytes
    run('Cli/snook', *options, 'call', 'export-json', json.dumps({
        'destinationPath': data + '/Snook/daemon.token'}), expected=2)
    # A second embedded owner must fail, and a missing daemon token must not fall back.
    run('Cli/snook', '--host', 'embedded', '--data-dir', data, 'doctor', expected=2)
    missing = root + '/missing-profile'
    run('Cli/snook', '--host', 'daemon', '--data-dir', missing, 'doctor', expected=2)
    assert not os.path.exists(missing)
    watch.send_signal(signal.SIGINT)
    assert watch.wait(timeout=10) == 130
    daemon.send_signal(signal.SIGTERM)
    assert daemon.wait(timeout=45) == 0
    assert not os.path.exists(data + '/Snook/daemon.endpoint.json')
    reopened = json.loads(run('Cli/snook', '--host', 'embedded', '--data-dir', data, 'bootstrap'))
    assert any(item['id'] == board['id'] for item in reopened['boards'])
finally:
    for process in (watch, daemon):
        if process is not None and process.poll() is None:
            process.kill()
            process.wait(timeout=10)
print('Payload help, discovery, create replay, schema-5 export/no-overwrite, CLI watch, fail-closed, shutdown and lease reopening passed.')
PY
[[ $(stat -c '%a' "$verify_dir/data/Snook/daemon.token") == 600 ]]
echo "RPM paths, launchers, user unit, documentation and token permissions passed."
echo "Artifacts retained at $verify_dir. This does not validate installation or a live systemd unit."
