#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
source scripts/environment.sh
if [[ -x "$HOME/.local/bin/studio-heavy-run" && "${PORTFOLIO_HEAVY_ACTIVE:-}" != 1 ]]; then
  exec "$HOME/.local/bin/studio-heavy-run" -- env PORTFOLIO_HEAVY_ACTIVE=1 "$0" "$@"
fi
python3 scripts/check-repository.py
dotnet restore BusinessData.sln --locked-mode
dotnet test BusinessData.sln -c Release --no-restore --nologo -m:2 --logger 'trx;LogFileName=tests.trx' --results-directory artifacts/test-results
python3 scripts/protocol-demo.py
