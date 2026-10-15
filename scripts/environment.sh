#!/usr/bin/env bash
# Sourced after the caller has changed to the repository root.
if [[ -f .env ]]; then
  set -a
  source .env
  set +a
fi
if [[ -n "${DOTNET_ROOT:-}" && -x "$DOTNET_ROOT/dotnet" ]]; then
  export PATH="$DOTNET_ROOT:$PATH"
fi
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1
# Reproducible local Python tooling without changing the host Python installation.
if [[ -x .venv/bin/python3 ]]; then
  export PATH="$PWD/.venv/bin:$PATH"
fi
