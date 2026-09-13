#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
output_dir="${SNOOK_SCREENSHOT_DIR:-${repo_root}/artifacts/ui-screenshots}"
data_dir="${SNOOK_DATA_DIR:-${repo_root}/artifacts/ui-screenshot-data}"

mkdir -p "${output_dir}" "${data_dir}"

echo "Capturing Snook UI screens to ${output_dir}"
dotnet build "${repo_root}/src/Snook.Screenshot/Snook.Screenshot.csproj" \
  --no-restore \
  -m:1 \
  -nr:false \
  -p:MSBuildEnableWorkloadResolver=false \
  -v:minimal

SNOOK_DATA_DIR="${data_dir}" \
SNOOK_SCREENSHOT_SEED="${SNOOK_SCREENSHOT_SEED:-1}" \
SNOOK_SCREENSHOT_DIR="${output_dir}" \
dotnet "${repo_root}/src/Snook.Screenshot/bin/Debug/net10.0/snook-screenshot.dll" "$@"

echo "Screenshots ready:"
find "${output_dir}" -maxdepth 1 -type f -name '*.png' -printf '  %f\n' | sort
