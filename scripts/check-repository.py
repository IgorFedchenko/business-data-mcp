#!/usr/bin/env python3
"""Check tracked source assets without needing network, an LLM, or production credentials."""
import json
from pathlib import Path
import re

root = Path(__file__).resolve().parents[1]
for path in [root / 'global.json', *root.glob('examples/*.json')]:
    json.loads(path.read_text())
for path in root.glob('src/**/*.cs'):
    if any(part in ('bin', 'obj') for part in path.parts):
        continue
    text = path.read_text()
    assert not re.search(r'(gh[pousr]_[A-Za-z0-9]{20,}|sk-ant-[A-Za-z0-9-]{20,}|AKIA[A-Z0-9]{16})', text), path
assert 'MIT License' in (root / 'LICENSE').read_text()
assert 'MCP_ALLOW_WRITES=false' in (root / '.env.example').read_text()
assert not any((root / 'examples').glob('*.db'))
print('Repository configuration and credential-pattern checks passed.')
