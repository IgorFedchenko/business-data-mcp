#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
# This entry owns the Studio ticket. Inner entries never acquire another lock.
if [[ -x "$HOME/.local/bin/studio-heavy-run" ]]; then
  exec "$HOME/.local/bin/studio-heavy-run" -- bash scripts/verify-inner.sh "$@"
fi
exec bash scripts/verify-inner.sh "$@"
