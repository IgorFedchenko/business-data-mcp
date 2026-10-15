#!/usr/bin/env python3
"""No-inference checks of capture argv and required recorded-replay gating."""

import importlib.util
import json
from pathlib import Path
import subprocess
import sys
import tempfile

ROOT = Path(__file__).resolve().parents[1]


def main():
    spec = importlib.util.spec_from_file_location(
        "capture_command_module", ROOT / "scripts/record-claude-demo.py"
    )
    capture = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(capture)  # Import does not authenticate or invoke its main().
    with tempfile.TemporaryDirectory(prefix="mcp-demo-contract-") as directory:
        fixture = Path(directory)
        config = fixture / "mcp.json"
        config.write_text(
            json.dumps(
                {
                    "mcpServers": {
                        "business-data": {"command": "never-executed", "env": {"MCP_ALLOW_WRITES": "false"}}
                    }
                }
            )
        )
        stub = fixture / "claude_stub.py"
        stub.write_text("""import json,sys
from pathlib import Path
args=sys.argv[1:]
assert args[:2] == ["-p", "scripted argv check; no inference"]
assert args.count("--strict-mcp-config") == 1
assert args[args.index("--strict-mcp-config")+1] == "--mcp-config", "strict flag must be boolean"
assert args.count("--mcp-config") == 1
config=Path(args[args.index("--mcp-config")+1])
loaded=json.loads(config.read_text())
assert set(loaded["mcpServers"]) == {"business-data"}
assert loaded["mcpServers"]["business-data"]["env"]["MCP_ALLOW_WRITES"] == "false"
assert set(args[args.index("--allowedTools")+1].split(",")) == {"mcp__business-data__get_order","mcp__business-data__get_customer_history"}
for flag,value in {"--tools":"", "--setting-sources":"", "--permission-mode":"dontAsk", "--max-turns":"6", "--output-format":"stream-json"}.items():
    assert args[args.index(flag)+1] == value, flag
for flag in ("--disable-slash-commands", "--no-session-persistence", "--no-chrome"):
    assert flag in args, flag
print("PASS: scripted CLI stub loaded explicit read-only config and retained restrictions; no inference")
""")
        argv = capture.capture_command("scripted argv check; no inference", config)
        assert argv[0] == "claude"
        subprocess.run([sys.executable, str(stub), *argv[1:]], check=True)
        # Removing explicit config must fail the independent stub before any model exists.
        broken = list(argv[1:])
        at = broken.index("--mcp-config")
        del broken[at : at + 2]
        rejected = subprocess.run([sys.executable, str(stub), *broken], capture_output=True, text=True)
        assert rejected.returncode != 0, "missing_mcp_config_negative_control_was_accepted"
        absent = fixture / "absent-capture.json"
        for flags in ([], ["--require-capture"]):
            result = subprocess.run(
                [sys.executable, str(ROOT / "scripts/evaluate.py"), "--capture", str(absent), *flags],
                capture_output=True,
                text=True,
            )
            assert result.returncode != 0, "missing_recorded_capture_exit_must_fail"
            assert "missing_recorded_capture:" in result.stderr, (
                "must fail named capture gate before host startup"
            )
        print("PASS: default/explicit strict modes reject missing recording; invalid config argv rejected")


if __name__ == "__main__":
    main()
