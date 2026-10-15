#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
command -v gitleaks >/dev/null || { echo 'Install the free gitleaks CLI before verification.' >&2; exit 1; }
gitleaks dir --redact --no-banner .
if [[ -d .git ]]; then gitleaks git --redact --no-banner .; fi
