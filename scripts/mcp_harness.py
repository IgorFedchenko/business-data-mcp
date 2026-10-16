"""Small real-process MCP harness. Synthetic fixtures only; no model calls."""

import json
import os
from pathlib import Path
import queue
import select
import socket
import subprocess
import threading
import time
import urllib.error
import urllib.request

PROTOCOL = "2025-11-25"


def payload(result):
    assert not result.get("isError"), result
    if "structuredContent" in result:
        structured = result["structuredContent"]
        # SDK 2.2 wraps non-object outputs for negotiated 2025-11-25 clients.
        if isinstance(structured, dict) and set(structured) == {"result"}:
            return structured["result"]
        return structured
    return json.loads(next(c["text"] for c in result["content"] if c["type"] == "text"))


def error_code(result):
    assert result.get("isError") is True, result
    return json.loads(result["content"][0]["text"])["code"]


class Server:
    def __init__(self, assembly, database, writes=False, http=False, keys=None, command=None):
        self.assembly = str(Path(assembly).resolve())
        self.env = {
            **os.environ,
            "MCP_DATABASE": str(database),
            "MCP_ALLOW_WRITES": str(writes).lower(),
            "MCP_TOOL_LIMIT": "1000",
            "MCP_HTTP_LIMIT": "1000",
        }
        # Never inherit a real HTTP key into a fixture subprocess.
        self.env.pop("MCP_READ_API_KEY", None)
        self.env.pop("MCP_WRITE_API_KEY", None)
        self.http = http
        self.sequence = 0
        self.stderr = []
        args = command or ["dotnet", self.assembly, "--transport", "http" if http else "stdio"]
        if http:
            with socket.socket() as listener:
                listener.bind(("127.0.0.1", 0))
                port = listener.getsockname()[1]
            self.url = f"http://127.0.0.1:{port}"
            self.env.update(MCP_READ_API_KEY=keys[0], MCP_WRITE_API_KEY=keys[1])
            args += ["--urls", self.url]
        self.process = subprocess.Popen(
            args,
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
            env=self.env,
        )
        self.responses = queue.Queue()

        def read_stdout():
            for line in self.process.stdout:
                if not http:
                    try:
                        self.responses.put(json.loads(line))
                    except json.JSONDecodeError:
                        self.responses.put({"invalid_stdout": True})

        def read_stderr():
            for line in self.process.stderr:
                self.stderr.append(line)

        self.readers = [
            threading.Thread(target=read_stdout, daemon=True),
            threading.Thread(target=read_stderr, daemon=True),
        ]
        for reader in self.readers:
            reader.start()
        if http:
            deadline = time.monotonic() + 20
            while True:
                try:
                    with urllib.request.urlopen(self.url + "/health", timeout=1) as response:
                        assert json.load(response)["status"] == "ok"
                    break
                except (OSError, ValueError):
                    if self.process.poll() is not None or time.monotonic() > deadline:
                        self.close()
                        raise AssertionError("Kestrel did not become healthy") from None
                    time.sleep(0.1)
        else:
            self.initialize()

    def initialize(self, key=None):
        negotiated = self.send(
            "initialize",
            {
                "protocolVersion": PROTOCOL,
                "capabilities": {},
                "clientInfo": {"name": "scripted-acceptance", "version": "1"},
            },
            key=key,
        )
        assert negotiated["protocolVersion"] == PROTOCOL, "unexpected negotiated protocol version"
        self.negotiated_protocol = negotiated["protocolVersion"]
        self.send("notifications/initialized", {}, notification=True, key=key)

    def raw_http(self, body, key=None, headers=None):
        request_headers = {
            "Content-Type": "application/json",
            "Accept": "application/json, text/event-stream",
            "MCP-Protocol-Version": PROTOCOL,
        }
        if key:
            request_headers["X-Api-Key"] = key
        request_headers.update(headers or {})
        request = urllib.request.Request(self.url + "/mcp", data=body, headers=request_headers)
        try:
            with urllib.request.urlopen(request, timeout=15) as response:
                return response.status, response.read().decode()
        except urllib.error.HTTPError as error:
            return error.code, error.read().decode()

    def send(self, method, params, notification=False, key=None):
        self.sequence += 1
        message = {"jsonrpc": "2.0", "method": method, "params": params}
        if not notification:
            message["id"] = self.sequence
        if self.http:
            status, body = self.raw_http(json.dumps(message).encode(), key)
            assert status in (200, 202), f"HTTP {status} for {method}"
            if notification:
                return None
            if body.startswith("event:") or body.startswith("data:"):
                messages = [
                    json.loads(line[5:].strip()) for line in body.splitlines() if line.startswith("data:")
                ]
                response = next(item for item in messages if item.get("id") == self.sequence)
            else:
                response = json.loads(body)
        else:
            self.process.stdin.write(json.dumps(message) + "\n")
            self.process.stdin.flush()
            if notification:
                return None
            while True:
                response = self.responses.get(timeout=15)
                assert "invalid_stdout" not in response, "stdio stdout must be JSON-RPC only"
                if response.get("id") == self.sequence:
                    break
        assert "error" not in response, response
        return response["result"]

    def call(self, name, args, key=None):
        return self.send("tools/call", {"name": name, "arguments": args}, key=key)

    def approve(self, intent_id, confirmation=None, expected_exit=0):
        """Exercise the actual interactive operator adapter with a PTY, never MCP."""
        import pty

        master, slave = pty.openpty()
        operator = subprocess.Popen(
            ["dotnet", self.assembly, "approve-return", intent_id],
            stdin=slave,
            stdout=slave,
            stderr=slave,
            env=self.env,
        )
        os.close(slave)
        output = b""
        deadline = time.monotonic() + 15
        try:
            while b"to approve:" not in output:
                assert time.monotonic() < deadline, "operator prompt timed out"
                ready, _, _ = select.select([master], [], [], 0.2)
                if ready:
                    output += os.read(master, 8192)
            assert intent_id.encode() in output and b"no payment" in output
            answer = intent_id if confirmation is None else confirmation
            os.write(master, answer.encode() + b"\n")
            assert operator.wait(timeout=10) == expected_exit, "unexpected operator approval outcome"
        finally:
            os.close(master)
            if operator.poll() is None:
                operator.kill()
                operator.wait()

    def close(self):
        if self.process.poll() is None:
            if self.http:
                self.process.terminate()
            else:
                self.process.stdin.close()
            try:
                self.process.wait(timeout=5)
            except subprocess.TimeoutExpired:
                self.process.kill()
                self.process.wait(timeout=5)
        for reader in self.readers:
            reader.join(timeout=2)
            assert not reader.is_alive(), "owned process reader did not stop"
        for stream in (self.process.stdin, self.process.stdout, self.process.stderr):
            if stream and not stream.closed:
                stream.close()

    def __enter__(self):
        return self

    def __exit__(self, *_):
        self.close()
