#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
source scripts/environment.sh

dotnet restore BusinessData.sln --locked-mode --disable-parallel
dotnet build BusinessData.sln --no-restore -c Release --nologo -m:2
