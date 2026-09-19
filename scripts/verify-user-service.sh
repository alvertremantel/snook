#!/usr/bin/env bash
# Run the shipped unit's properties as a disposable transient user service.
# Argument: the extraction directory printed by verify-rpm.sh.
set -euo pipefail
if [[ $# -ne 1 ]]; then
  echo "Usage: $0 EXTRACTED_RPM_DIRECTORY" >&2
  exit 2
fi
payload_root=$(realpath -- "$1")
daemon="$payload_root/usr/lib64/snook/Daemon/snookd"
cli="$payload_root/usr/lib64/snook/Cli/snook"
unit_file="$payload_root/usr/lib/systemd/user/snookd.service"
[[ -x "$daemon" && -x "$cli" && -f "$unit_file" ]]
manager_state=$(systemctl --user is-system-running || true)
[[ "$manager_state" == running || "$manager_state" == degraded ]]
test_dir=$(mktemp -d /tmp/snook-systemd-check.XXXXXXXX)
unit="$(basename "$test_dir").service"
echo "Disposable service: $unit; data: $test_dir"

# Preserve every shipped Unit/Service setting except the executable location;
# the package has not been installed. Never import the Install section.
properties=()
section=''
while IFS= read -r line; do
  case "$line" in
    '[Unit]'|'[Service]'|'[Install]') section="$line"; continue ;;
    ''|'#'*) continue ;;
    ExecStart=*) continue ;;
  esac
  if [[ "$section" == '[Unit]' || "$section" == '[Service]' ]]; then
    properties+=(--property "$line")
  fi
done < "$unit_file"
port=$(python3 -c 'import socket; s=socket.socket(); s.bind(("127.0.0.1",0)); print(s.getsockname()[1]); s.close()')
cleanup() {
  systemctl --user stop "$unit" >/dev/null 2>&1 || true
}
trap cleanup EXIT
systemd-run --user --collect --unit "$unit" "${properties[@]}" \
  "$daemon" --data-dir "$test_dir/data" --port "$port"

# Use explicit client configuration so shell/user-manager defaults cannot select
# a real profile. The daemon's CLI arguments likewise override its environment.
client=(env -u SNOOK_DAEMON_TOKEN -u SNOOK_DAEMON_TOKEN_FILE \
  "$cli" --host daemon --data-dir "$test_dir/data" \
  --endpoint "http://127.0.0.1:$port/" --token-file "$test_dir/data/Snook/daemon.token")
ready() {
  for attempt in {1..30}; do
    if timeout 5 "${client[@]}" doctor > "$test_dir/doctor.json" 2> "$test_dir/doctor.error.json"; then
      grep -q '"host": "daemon-client"' "$test_dir/doctor.json"
      return
    fi
    systemctl --user is-active --quiet "$unit" || return 1
    sleep 0.2
  done
  echo "Daemon did not become ready; inspect the disposable unit journal." >&2
  return 1
}
ready
settings=$(systemctl --user show "$unit" -p Type -p UMask -p NoNewPrivileges -p ActiveState)
for setting in Type=exec UMask=0077 NoNewPrivileges=yes ActiveState=active; do
  grep -qxF "$setting" <<< "$settings"
done
printf '%s\n' "$settings"
timeout 10 "${client[@]}" call create-board '{"name":"Managed service smoke"}' > "$test_dir/created.json"
systemctl --user restart "$unit"
ready
timeout 10 "${client[@]}" bootstrap > "$test_dir/after-restart.json"
grep -q 'Managed service smoke' "$test_dir/after-restart.json"
journalctl --user -u "$unit" --no-pager -o cat > "$test_dir/journal.txt"
grep -q '"ready":true' "$test_dir/journal.txt"
if grep -qFf "$test_dir/data/Snook/daemon.token" "$test_dir/journal.txt"; then
  echo "Daemon token leaked into the disposable service journal." >&2
  exit 1
fi
systemctl --user stop "$unit"
[[ ! -e "$test_dir/data/Snook/daemon.endpoint.json" ]]
timeout 10 "$cli" --host embedded --data-dir "$test_dir/data" bootstrap > "$test_dir/after-stop.json"
grep -q 'Managed service smoke' "$test_dir/after-stop.json"
[[ $(stat -c '%a' "$test_dir/data/Snook/daemon.token") == 600 ]]
echo "Transient user service readiness, private token, restart, stop and lease reopening passed."
echo "Artifacts retained at $test_dir. No service was installed or enabled."
