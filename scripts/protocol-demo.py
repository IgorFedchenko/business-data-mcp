#!/usr/bin/env python3
"""Real JSON-RPC/stdio smoke demo. No Claude, LLM API, npm or network required."""

import argparse
import json
import os
from pathlib import Path
import queue
import subprocess
import tempfile
import threading

parser = argparse.ArgumentParser()
parser.add_argument(
    "--assembly", default="src/BusinessData.Server/bin/Release/net8.0/BusinessData.Server.dll"
)
options = parser.parse_args()

with tempfile.TemporaryDirectory(prefix="business-mcp-demo-") as fixture:
    env = {**os.environ, "MCP_DATABASE": str(Path(fixture) / "demo.db"), "MCP_ALLOW_WRITES": "false"}
    process = subprocess.Popen(
        ["dotnet", str(Path(options.assembly).resolve()), "--transport", "stdio"],
        stdin=subprocess.PIPE,
        stdout=subprocess.PIPE,
        stderr=subprocess.DEVNULL,
        text=True,
        env=env,
    )
    responses = queue.Queue()

    def read_lines():
        for line in process.stdout:
            try:
                responses.put(json.loads(line))
            except json.JSONDecodeError:
                responses.put({"invalid_stdout": True})

    threading.Thread(target=read_lines, daemon=True).start()

    def send(method, params, request_id=None):
        message = {"jsonrpc": "2.0", "method": method, "params": params}
        if request_id is not None:
            message["id"] = request_id
        process.stdin.write(json.dumps(message) + "\n")
        process.stdin.flush()
        if request_id is None:
            return None
        while True:
            response = responses.get(timeout=15)
            assert "invalid_stdout" not in response, "stdout must contain only JSON-RPC"
            if response.get("id") == request_id:
                assert "error" not in response, response
                return response["result"]

    try:
        send(
            "initialize",
            {
                "protocolVersion": "2025-11-25",
                "capabilities": {},
                "clientInfo": {"name": "business-data-smoke", "version": "1.0.0"},
            },
            1,
        )
        send("notifications/initialized", {})
        tools = send("tools/list", {}, 2)["tools"]
        assert len(tools) == 4, tools
        assert all(t["annotations"]["readOnlyHint"] for t in tools)
        order = send("tools/call", {"name": "get_order", "arguments": {"orderId": "ORD-1001"}}, 3)
        assert not order.get("isError"), order
        assert "Avery Morgan" in json.dumps(order), order
        denied = send(
            "tools/call",
            {
                "name": "prepare_return",
                "arguments": {
                    "orderId": "ORD-1001",
                    "reason": "damaged",
                    "idempotencyKey": "demo-prepare-0001",
                },
            },
            4,
        )
        assert denied.get("isError") is True and "forbidden" in json.dumps(denied), denied
        print("stdio MCP: 4 read-only tools discovered; ORD-1001 read; unauthorized return blocked.")
    finally:
        process.stdin.close()
        try:
            process.wait(timeout=5)
        except subprocess.TimeoutExpired:
            process.terminate()
            process.wait(timeout=5)
