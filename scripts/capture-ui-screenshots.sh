#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
output_dir="${repo_root}/artifacts/ui-screenshots"
data_dir="${repo_root}/artifacts/ui-screenshot-data"

mkdir -p "${output_dir}" "${data_dir}"

echo "Capturing Snook UI screens to ${output_dir}"
SNOOK_DATA_DIR="${data_dir}" \
SNOOK_SCREENSHOT_SEED="${SNOOK_SCREENSHOT_SEED:-1}" \
SNOOK_SCREENSHOT_DIR="${output_dir}" \
dotnet run --project "${repo_root}/src/Snook.Screenshot/Snook.Screenshot.csproj" \
  --no-restore \
  -p:MSBuildEnableWorkloadResolver=false \
  -p:UseAppHost=false \
  "$@"

echo "Screenshots ready:"
find "${output_dir}" -maxdepth 1 -type f -name '*.png' -printf '  %f\n' | sort
