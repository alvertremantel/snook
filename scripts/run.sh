#!/usr/bin/env bash
set -euo pipefail

ROOT=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
PROJECT="$ROOT/src/Snook.Desktop/Snook.Desktop.csproj"

dotnet build "$PROJECT" -p:MSBuildEnableWorkloadResolver=false
exec dotnet run --project "$PROJECT" --no-build -- "$@"
