#!/usr/bin/env bash
# Opt-in native Fedora install/upgrade/remove verification in a disposable Podman container.
set -euo pipefail

if [[ "${SNOOK_VERIFY_RPM_LIFECYCLE:-0}" != "1" ]]; then
  echo "SKIP: set SNOOK_VERIFY_RPM_LIFECYCLE=1 to run disposable native RPM lifecycle verification"
  exit 0
fi
if [[ $# -ne 2 ]]; then
  echo "Usage: $0 PREVIOUS_RPM CURRENT_RPM" >&2
  exit 2
fi

for tool in podman rpm realpath; do
  command -v "$tool" >/dev/null
done

previous_rpm=$(realpath -- "$1")
current_rpm=$(realpath -- "$2")
[[ -f "$previous_rpm" && -f "$current_rpm" ]]
previous_nevra=$(rpm -qp --qf '%{NAME}-%{VERSION}-%{RELEASE}.%{ARCH}' "$previous_rpm")
current_nevra=$(rpm -qp --qf '%{NAME}-%{VERSION}-%{RELEASE}.%{ARCH}' "$current_rpm")
[[ "$previous_nevra" == snook-* && "$current_nevra" == snook-* && "$previous_nevra" != "$current_nevra" ]]

image=${SNOOK_RPM_LIFECYCLE_IMAGE:-registry.fedoraproject.org/fedora:44}
container="snook-rpm-lifecycle-${RANDOM}-$$"
cleanup() { podman rm -f "$container" >/dev/null 2>&1 || true; }
trap cleanup EXIT INT TERM

podman create --name "$container" "$image" sleep infinity >/dev/null
podman start "$container" >/dev/null
podman cp "$previous_rpm" "$container:/tmp/snook-previous.rpm"
podman cp "$current_rpm" "$container:/tmp/snook-current.rpm"

podman exec "$container" bash -euc '
  test ! -e /root/.local/share/Snook
  ! command -v tailscale >/dev/null

  dnf -y --setopt=install_weak_deps=False install /tmp/snook-previous.rpm
  previous=$(rpm -q --qf "%{NAME}-%{VERSION}-%{RELEASE}.%{ARCH}" snook)
  test -x /usr/bin/snook
  test -x /usr/bin/snook-cli
  test -x /usr/bin/snookd
  test -x /usr/lib64/snook/Desktop/Snook
  test -x /usr/lib64/snook/Cli/snook
  test -x /usr/lib64/snook/Daemon/snookd
  test -f /usr/share/applications/com.snook.Snook.desktop
  test -f /usr/lib/systemd/user/snookd.service
  test -f /usr/share/doc/snook/docs/tailscale-threat-model.md
  test ! -e /root/.local/share/Snook
  ! command -v tailscale >/dev/null

  dnf -y --setopt=install_weak_deps=False upgrade /tmp/snook-current.rpm
  current=$(rpm -q --qf "%{NAME}-%{VERSION}-%{RELEASE}.%{ARCH}" snook)
  test "$previous" != "$current"
  test -x /usr/bin/snook-cli
  test -x /usr/bin/secret-tool
  rpm -q --whatprovides /usr/bin/secret-tool >/dev/null
  grep -q "Tailnet daemon" /usr/share/doc/snook/README.md
  test ! -e /root/.local/share/Snook
  ! command -v tailscale >/dev/null

  dnf -y remove snook
  ! rpm -q snook >/dev/null 2>&1
  test ! -e /usr/bin/snook
  test ! -e /usr/bin/snook-cli
  test ! -e /usr/bin/snookd
  test ! -e /usr/lib64/snook
  test ! -e /usr/share/applications/com.snook.Snook.desktop
  test ! -e /usr/lib/systemd/user/snookd.service
  test ! -e /usr/share/doc/snook
  test ! -e /root/.local/share/Snook
  ! command -v tailscale >/dev/null

  printf "Native lifecycle passed: %s -> %s -> removed\n" "$previous" "$current"
'

echo "PASS: disposable Fedora RPM install, upgrade, path inspection, and removal"
echo "NOTE: no host package, service, workspace, or Tailscale state was changed"
