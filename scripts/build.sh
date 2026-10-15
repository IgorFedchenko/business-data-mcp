#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
source scripts/environment.sh
if [[ -x "$HOME/.local/bin/studio-heavy-run" && "${PORTFOLIO_HEAVY_ACTIVE:-}" != 1 ]]; then
  exec "$HOME/.local/bin/studio-heavy-run" -- env PORTFOLIO_HEAVY_ACTIVE=1 "$0" "$@"
fi
dotnet restore BusinessData.sln --locked-mode
dotnet build BusinessData.sln --no-restore -c Release --nologo -m:2
