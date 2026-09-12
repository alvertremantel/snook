#!/usr/bin/env bash
set -euo pipefail

ROOT=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
RPM_DIR=${SNOOK_RPM_OUT_DIR:-"$ROOT/artifacts/rpm"}

rpm_file=$(find "$RPM_DIR" -maxdepth 1 -type f -name '*.rpm' -printf '%T@ %p\n' 2>/dev/null \
  | sort -nr | head -n 1 | cut -d' ' -f2-)

if [[ -z "$rpm_file" ]]; then
  echo "No packaged RPM found in $RPM_DIR. Run scripts/package-rpm.sh first." >&2
  exit 1
fi

sudo dnf install -y "$rpm_file"
