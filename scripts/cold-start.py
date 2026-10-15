#!/usr/bin/env python3
"""Outer cold-start check. Never wrap: copied run.sh owns its build.sh queue ticket."""

import json
import os
from pathlib import Path
import queue
import shutil
import subprocess
import tempfile
import threading
import time

from eval_contract import ROOT, check_order
from mcp_harness import PROTOCOL, payload


def main():
    paths = subprocess.check_output(["git", "ls-files", "-z"], cwd=ROOT).decode().split("\0")
    with tempfile.TemporaryDirectory(prefix="business-cold-start-") as directory:
        target = Path(directory)
        for name in filter(None, paths):
            source = ROOT / name
            if source.is_file():
                destination = target / name
                destination.parent.mkdir(parents=True, exist_ok=True)
                shutil.copy2(source, destination)
        assert not list(target.rglob("bin")) and not list(target.rglob("obj"))
        assert not (target / ".data").exists()
        env = {**os.environ, "MCP_DATABASE": str(target / ".data/cold.db"), "MCP_ALLOW_WRITES": "false"}
        for name in ("MCP_READ_API_KEY", "MCP_WRITE_API_KEY"):
            env.pop(name, None)
        child = subprocess.Popen(
            ["bash", "scripts/run.sh"],
            cwd=target,
            env=env,
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
        )
        events = queue.Queue()

        def read(stream, kind):
            for line in stream:
                events.put((kind, line))

        readers = [
            threading.Thread(target=read, args=(child.stdout, "stdout")),
            threading.Thread(target=read, args=(child.stderr, "stderr")),
        ]
        for reader in readers:
            reader.start()
        message = {
            "jsonrpc": "2.0",
            "id": 1,
            "method": "initialize",
            "params": {
                "protocolVersion": PROTOCOL,
                "capabilities": {},
                "clientInfo": {"name": "cold-start", "version": "1"},
            },
        }
        child.stdin.write(json.dumps(message) + "\n")
        child.stdin.flush()
        # Queue waiting is unbounded by design; startup guard begins after the build succeeds.
        active_deadline = None
        try:
            while True:
                if child.poll() is not None:
                    raise AssertionError("cold command exited before initialization")
                if active_deadline is not None:
                    assert time.monotonic() < active_deadline, "cold startup after build exceeded 30s"
                try:
                    kind, line = events.get(timeout=1)
                except queue.Empty:
                    continue
                if kind == "stderr":
                    if "Waiting in Studio" in line:
                        print("cold-start: queued build, no runtime result", flush=True)
                    if "Build succeeded." in line:
                        active_deadline = time.monotonic() + 30
                    continue
                response = json.loads(line)
                if response.get("id") == 1:
                    assert response["result"]["protocolVersion"] == PROTOCOL
                    break
            for message in (
                {"jsonrpc": "2.0", "method": "notifications/initialized", "params": {}},
                {
                    "jsonrpc": "2.0",
                    "id": 2,
                    "method": "tools/call",
                    "params": {"name": "get_order", "arguments": {"orderId": "ORD-1001"}},
                },
            ):
                child.stdin.write(json.dumps(message) + "\n")
            child.stdin.flush()
            deadline = time.monotonic() + 10
            while True:
                assert time.monotonic() < deadline, "cold read timeout"
                kind, line = events.get(timeout=10)
                if kind == "stdout":
                    response = json.loads(line)
                    if response.get("id") == 2:
                        check_order(payload(response["result"]))
                        break
            assert (target / ".data/cold.db").is_file()
        finally:
            child.stdin.close()
            try:
                child.wait(timeout=10)
            except subprocess.TimeoutExpired:
                child.kill()
                child.wait(timeout=10)
            for reader in readers:
                reader.join(timeout=2)
                assert not reader.is_alive()
            child.stdout.close()
            child.stderr.close()
    print("PASS: clean tracked copy, documented run.sh built through its own ticket, real stdio/frozen facts")


if __name__ == "__main__":
    main()
