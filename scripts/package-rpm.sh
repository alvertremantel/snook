#!/usr/bin/env bash
set -euo pipefail

ROOT=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
VERSION=${SNOOK_RPM_VERSION:-0.1.0}
ARCH=${SNOOK_RPM_ARCH:-linux-x64}
OUT_DIR=${SNOOK_RPM_OUT_DIR:-"$ROOT/artifacts/rpm"}
WORK_DIR=${SNOOK_RPM_WORK_DIR:-"$ROOT/.rpm-work"}

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

rm -rf "$WORK_DIR"
mkdir -p "$WORK_DIR/SOURCES" "$WORK_DIR/SPECS" "$WORK_DIR/BUILD" \
  "$WORK_DIR/BUILDROOT" "$WORK_DIR/RPMS" "$WORK_DIR/SRPMS" "$OUT_DIR"

dotnet publish "$ROOT/src/Snook.Desktop/Snook.Desktop.csproj" \
  --configuration Release --runtime "$ARCH" --self-contained true \
  -p:PublishTrimmed=false -p:PublishSingleFile=false \
  -p:DebugSymbols=false -p:DebugType=None \
  --output "$WORK_DIR/publish"

tar -C "$WORK_DIR" -czf \
  "$WORK_DIR/SOURCES/Snook-${VERSION}-${ARCH}.tar.gz" \
  --transform="s,^publish,Snook-${VERSION}-${ARCH}," publish
cp "$ROOT/packaging/rpm/snook.spec" "$WORK_DIR/SPECS/"
cp "$ROOT/packaging/rpm/com.snook.Snook.desktop" "$WORK_DIR/SOURCES/"

rpmbuild -bb "$WORK_DIR/SPECS/snook.spec" \
  --define "_topdir $WORK_DIR" \
  --define "_version $VERSION"
find "$WORK_DIR/RPMS" -name "snook-${VERSION}-*.rpm" -exec cp {} "$OUT_DIR/" \;
echo "RPM written to $OUT_DIR"

if [[ ${1:-} == --install ]]; then
  rpm_file=$(find "$OUT_DIR" -maxdepth 1 -name '*.rpm' -print -quit)
  if [[ -z "$rpm_file" ]]; then
    echo "No RPM was produced." >&2
    exit 1
  fi
  sudo dnf install -y "$rpm_file"
fi
