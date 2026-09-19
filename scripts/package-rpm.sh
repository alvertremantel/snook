#!/usr/bin/env bash
set -euo pipefail

ROOT=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
VERSION=${SNOOK_RPM_VERSION:-0.1.0}
ARCH=${SNOOK_RPM_ARCH:-linux-x64}
OUT_DIR=${SNOOK_RPM_OUT_DIR:-"$ROOT/artifacts/rpm"}
WORK_PARENT=${SNOOK_RPM_WORK_DIR:-"$ROOT/.rpm-work"}

if [[ $# -gt 1 || ($# -eq 1 && $1 != --install) ]]; then
  echo "Usage: $0 [--install]" >&2
  exit 2
fi
if [[ ! "$VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
  echo "SNOOK_RPM_VERSION must be a numeric major.minor.patch version." >&2
  exit 2
fi

if [[ "$ARCH" != linux-x64 ]]; then
  echo "Only linux-x64 is currently supported by the RPM spec." >&2
  exit 2
fi
if ! command -v rpmbuild >/dev/null 2>&1; then
  cat >&2 <<'EOF'
rpmbuild is required to create an RPM. On Fedora, install it with:
  sudo dnf install rpm-build
Then rerun this script.
EOF
  exit 127
fi

mkdir -p "$WORK_PARENT" "$OUT_DIR"
WORK_PARENT=$(cd "$WORK_PARENT" && pwd)
OUT_DIR=$(cd "$OUT_DIR" && pwd)
WORK_DIR=$(mktemp -d "$WORK_PARENT/build.XXXXXXXX")
echo "Fresh packaging workspace: $WORK_DIR"
mkdir -p "$WORK_DIR/SOURCES" "$WORK_DIR/SPECS" "$WORK_DIR/BUILD" \
  "$WORK_DIR/BUILDROOT" "$WORK_DIR/RPMS" "$WORK_DIR/SRPMS" "$OUT_DIR"

for component in Desktop Cli Daemon; do
  dotnet publish "$ROOT/src/Snook.$component/Snook.$component.csproj" \
    --configuration Release --runtime "$ARCH" --self-contained true \
    -p:MSBuildEnableWorkloadResolver=false -m:1 -nr:false \
    -p:PublishTrimmed=false -p:PublishSingleFile=false \
    -p:DebugSymbols=false -p:DebugType=None \
    --output "$WORK_DIR/publish/$component"
done

tar -C "$WORK_DIR" -czf \
  "$WORK_DIR/SOURCES/Snook-${VERSION}-${ARCH}.tar.gz" \
  --transform="s,^publish,Snook-${VERSION}-${ARCH}," publish
cp "$ROOT/packaging/rpm/snook.spec" "$WORK_DIR/SPECS/"
cp "$ROOT/packaging/rpm/com.snook.Snook.desktop" "$WORK_DIR/SOURCES/"
cp "$ROOT/deploy/systemd/snookd.service" "$WORK_DIR/SOURCES/"
cp "$ROOT/README.md" "$ROOT/docs/operations.md" "$ROOT/docs/cli.md" "$ROOT/docs/json-export.md" "$WORK_DIR/SOURCES/"

rpmbuild -bb "$WORK_DIR/SPECS/snook.spec" \
  --define "_topdir $WORK_DIR" \
  --define "snook_version $VERSION"
mapfile -t rpm_files < <(find "$WORK_DIR/RPMS" -type f -name "snook-${VERSION}-*.rpm")
if [[ ${#rpm_files[@]} -ne 1 ]]; then
  echo "Expected exactly one Snook RPM from this build." >&2
  exit 1
fi
rpm_file="$OUT_DIR/$(basename "${rpm_files[0]}")"
cp -- "${rpm_files[0]}" "$rpm_file"
echo "RPM written to $rpm_file"

if [[ ${1:-} == --install ]]; then
  sudo dnf install -y "$rpm_file"
fi
