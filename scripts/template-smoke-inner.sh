#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
source scripts/environment.sh

repository="$PWD"
fixture="$(mktemp -d)"
trap 'rm -rf "$fixture"' EXIT
export DOTNET_CLI_HOME="$fixture/cli-home"
dotnet new install "$repository" --debug:custom-hive "$fixture/hive"
dotnet new business-mcp -n ContosoMcp -o "$fixture/generated" --debug:custom-hive "$fixture/hive"
cd "$fixture/generated"
[[ -f CLAUDE.md && -f AGENTS.md && ! -e docs/implementation-plan.md && ! -e docs/verification.md ]]
python3 -c 'from pathlib import Path; assert "CLAUDE.md" in Path("AGENTS.md").read_text()'
python3 scripts/check-repository.py
dotnet restore ContosoMcp.sln --locked-mode --disable-parallel
dotnet build src/ContosoMcp.Server/ContosoMcp.Server.csproj -c Release --no-restore --nologo -m:2
python3 scripts/protocol-demo.py --assembly src/ContosoMcp.Server/bin/Release/net8.0/ContosoMcp.Server.dll
