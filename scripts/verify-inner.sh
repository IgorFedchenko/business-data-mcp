#!/usr/bin/env bash
# Non-wrapping entry: on Studio invoke only via verify.sh or studio-heavy-run.
set -euo pipefail
cd "$(dirname "$0")/.."
source scripts/environment.sh
python3 scripts/check-repository.py
python3 scripts/check-demo-contracts.py
python3 scripts/check-capture-guard.py
python3 scripts/check-eval-negatives.py
python3 -m ruff check scripts
python3 -m ruff format --check scripts
./scripts/secret-scan.sh
dotnet restore BusinessData.sln --locked-mode --disable-parallel
dotnet format BusinessData.sln --verify-no-changes --no-restore
dotnet build BusinessData.sln -c Release --no-restore --nologo -m:2 -warnaserror
dotnet test BusinessData.sln -c Release --no-build --nologo -m:2 --logger 'trx;LogFileName=tests.trx' --results-directory artifacts/test-results
python3 scripts/process-acceptance.py
python3 scripts/crash-acceptance.py
python3 scripts/evaluate.py
bash scripts/template-smoke-inner.sh
python3 scripts/docker-smoke.py
