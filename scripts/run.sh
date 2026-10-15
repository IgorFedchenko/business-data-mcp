#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
source scripts/environment.sh
assembly=src/BusinessData.Server/bin/Release/net8.0/BusinessData.Server.dll
if [[ ! -f "$assembly" ]]; then
  ./scripts/build.sh >&2
fi
exec dotnet "$assembly" "$@"
