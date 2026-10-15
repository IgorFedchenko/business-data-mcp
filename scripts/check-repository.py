#!/usr/bin/env python3
"""Parse public configuration and assert deliverable hygiene; not a secret scanner."""

import json
from pathlib import Path
import re
import xml.etree.ElementTree as ET
import yaml

ROOT = Path(__file__).resolve().parents[1]
for path in ROOT.rglob("*"):
    if not path.is_file() or any(
        part in {".git", ".venv", "bin", "obj", ".data", "artifacts"} for part in path.relative_to(ROOT).parts
    ):
        continue
    if path.suffix in {".cs", ".py", ".sh", ".json", ".yml", ".yaml", ".toml"}:
        contents = path.read_text()
        # NuGet emits lockfiles without a final newline; preserve generated formatting.
        if path.name != "packages.lock.json":
            assert contents.endswith("\n"), f"missing final newline: {path}"
        assert all(line == line.rstrip() for line in contents.splitlines()), f"trailing whitespace: {path}"
    if path.suffix == ".json":
        json.loads(path.read_text())
    if path.suffix in {".yaml", ".yml"}:
        yaml.safe_load(path.read_text())
    if path.suffix in {".csproj", ".props"}:
        ET.fromstring(path.read_text())
    if (
        path.suffix == ".md"
        and path.name != "CLAUDE.md"
        and path.name not in {"implementation-plan.md", "verification.md"}
    ):
        for link in re.findall(r"\]\(([^)]+)\)", path.read_text()):
            if "://" not in link and not link.startswith("#"):
                assert (path.parent / link.split("#")[0]).exists(), (
                    f"broken documentation link: {path}: {link}"
                )
assert "MIT License" in (ROOT / "LICENSE").read_text()
assert "MCP_ALLOW_WRITES=false" in (ROOT / ".env.example").read_text()
print("PASS: JSON/YAML/XML, relative documentation links and reference configuration")
