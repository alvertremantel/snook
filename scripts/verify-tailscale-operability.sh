#!/usr/bin/env bash
set -euo pipefail

if [[ "${SNOOK_VERIFY_TAILSCALE:-0}" != "1" ]]; then
  echo "SKIP: set SNOOK_VERIFY_TAILSCALE=1 for read-only live Tailscale/Serve verification"
  exit 0
fi

cli="${SNOOK_CLI_BIN:-snook-cli}"
data_root="${SNOOK_DATA_DIR:?SNOOK_DATA_DIR must name a disposable running snookd profile}"
output="${SNOOK_TAILSCALE_VERIFY_OUTPUT:-/tmp/snook-tailscale-status.json}"

command -v tailscale >/dev/null
tailscale status --json >/dev/null
tailscale serve status --json >/dev/null

"${cli}" --no-profile --host daemon --data-dir "${data_root}" tailnet status >"${output}"
grep -q '"serveOrigin"' "${output}"
grep -q '"upstream"' "${output}"
grep -q '"funnel": "unsupported-public"' "${output}"
grep -q '"grantTemplate"' "${output}"
grep -q '"nextSteps"' "${output}"
grep -q '"hostPairingCommand"' "${output}"
grep -q '"clientPairingCommand"' "${output}"

token_file="${data_root}/Snook/daemon.token"
if [[ -f "${token_file}" ]] && grep -qFf "${token_file}" "${output}"; then
  echo "FAIL: diagnostic output exposed daemon.token" >&2
  exit 1
fi

echo "PASS: read-only Tailscale identity/Serve inspection and secret-free Snook status"
echo "NOTE: this script does not apply Serve, edit policy, enable Funnel, or prove an allowed/denied second node"
