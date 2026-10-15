#!/usr/bin/env python3
"""Docker non-root health/auth/read smoke. Run only inside the Studio heavy queue."""

import json
import os
from pathlib import Path
import secrets
import ssl
import subprocess
import tempfile
import time
import urllib.error
import urllib.request
from mcp_harness import PROTOCOL, payload
from eval_contract import check_order


def main():
    subprocess.run(["docker", "build", "-t", "business-data-mcp:smoke", "."], check=True)
    with tempfile.TemporaryDirectory() as directory:
        key = secrets.token_hex(32)
        env = Path(directory) / "container.env"
        env.write_text("MCP_READ_API_KEY=" + key + "\n")
        os.chmod(env, 0o600)
        tls = Path(directory) / "tls"
        tls.mkdir(mode=0o755)
        certificate, private_key = tls / "cert.pem", tls / "key.pem"
        subprocess.run(
            [
                "openssl",
                "req",
                "-x509",
                "-newkey",
                "rsa:2048",
                "-nodes",
                "-days",
                "1",
                "-keyout",
                str(private_key),
                "-out",
                str(certificate),
                "-subj",
                "/CN=localhost",
                "-addext",
                "subjectAltName=IP:127.0.0.1",
            ],
            check=True,
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
        )
        certificate.chmod(0o444)
        private_key.chmod(0o444)  # Ephemeral fixture; container's non-root user must read it.
        context = ssl.create_default_context(cafile=str(certificate))
        with env.open("a") as file:
            file.write(
                "ASPNETCORE_URLS=https://0.0.0.0:8080\n"
                "Kestrel__Certificates__Default__Path=/run/tls/cert.pem\n"
                "Kestrel__Certificates__Default__KeyPath=/run/tls/key.pem\n"
            )
        container = subprocess.check_output(
            [
                "docker",
                "run",
                "--detach",
                "--rm",
                "--env-file",
                str(env),
                "-p",
                "127.0.0.1::8080",
                "-v",
                str(tls) + ":/run/tls:ro",
                "business-data-mcp:smoke",
            ],
            text=True,
        ).strip()
        try:
            user = subprocess.check_output(["docker", "exec", container, "id", "-u"], text=True).strip()
            assert user != "0", "container must not run as root"
            binding = subprocess.check_output(["docker", "port", container, "8080/tcp"], text=True).strip()
            url = "https://" + binding
            for attempt in range(100):
                try:
                    with urllib.request.urlopen(url + "/health", timeout=1, context=context) as response:
                        assert json.load(response)["status"] == "ok"
                    break
                except OSError:
                    if attempt == 99:
                        raise
                    time.sleep(0.1)
            body = json.dumps(
                {
                    "jsonrpc": "2.0",
                    "id": 1,
                    "method": "tools/call",
                    "params": {"name": "get_order", "arguments": {"orderId": "ORD-1001"}},
                }
            ).encode()

            def request(api_key=None, request_body=None):
                headers = {
                    "Content-Type": "application/json",
                    "Accept": "application/json, text/event-stream",
                    "MCP-Protocol-Version": PROTOCOL,
                }
                if api_key:
                    headers["X-Api-Key"] = api_key
                return urllib.request.urlopen(
                    urllib.request.Request(url + "/mcp", data=request_body or body, headers=headers),
                    timeout=10,
                    context=context,
                )

            try:
                request()
            except urllib.error.HTTPError as error:
                assert error.code == 401
            else:
                raise AssertionError("missing key accepted")
            initialization = json.dumps(
                {
                    "jsonrpc": "2.0",
                    "id": 2,
                    "method": "initialize",
                    "params": {
                        "protocolVersion": PROTOCOL,
                        "capabilities": {},
                        "clientInfo": {"name": "docker-smoke", "version": "1"},
                    },
                }
            ).encode()
            with request(key, initialization) as response:
                assert response.status == 200
            with request(key) as response:
                raw = response.read().decode()
                if raw.startswith("event:") or raw.startswith("data:"):
                    result = next(
                        json.loads(line[5:].strip()) for line in raw.splitlines() if line.startswith("data:")
                    )
                else:
                    result = json.loads(raw)
                check_order(payload(result["result"]))
            mounts = json.loads(subprocess.check_output(["docker", "inspect", container], text=True))[0][
                "Mounts"
            ]
            assert any(mount["Destination"] == "/data" and mount["Type"] == "volume" for mount in mounts)
            subprocess.run(["docker", "exec", container, "test", "-s", "/data/business.db"], check=True)
            subprocess.run(["docker", "restart", container], check=True, stdout=subprocess.DEVNULL)
            for attempt in range(100):
                try:
                    with request(key) as response:
                        assert response.status == 200
                    break
                except OSError:
                    if attempt == 99:
                        raise
                    time.sleep(0.1)
            assert key not in subprocess.check_output(
                ["docker", "history", "--no-trunc", "business-data-mcp:smoke"], text=True
            )
            assert key not in subprocess.check_output(
                ["docker", "logs", container], stderr=subprocess.STDOUT, text=True
            )
        finally:
            subprocess.run(["docker", "stop", container], check=True, stdout=subprocess.DEVNULL)
    print("PASS: Docker build, non-root runtime, health, auth and real MCP read")


if __name__ == "__main__":
    main()
