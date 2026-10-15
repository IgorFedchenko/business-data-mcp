#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
source scripts/environment.sh
if [[ -x "$HOME/.local/bin/studio-heavy-run" && "${PORTFOLIO_HEAVY_ACTIVE:-}" != 1 ]]; then
  exec "$HOME/.local/bin/studio-heavy-run" -- env PORTFOLIO_HEAVY_ACTIVE=1 "$0" "$@"
fi
repository="$PWD"
fixture="$(mktemp -d)"
trap 'rm -rf "$fixture"' EXIT
dotnet new install "$repository" --debug:custom-hive "$fixture/hive"
dotnet new business-mcp -n ContosoMcp -o "$fixture/generated" --debug:custom-hive "$fixture/hive"
cd "$fixture/generated"
dotnet restore ContosoMcp.sln --locked-mode
dotnet build src/ContosoMcp.Server/ContosoMcp.Server.csproj -c Release --no-restore --nologo -m:2
python3 scripts/protocol-demo.py --assembly src/ContosoMcp.Server/bin/Release/net8.0/ContosoMcp.Server.dll
