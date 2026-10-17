#!/usr/bin/env python3
"""Docker non-root health/auth/read smoke. Run only inside the Studio heavy queue."""

import json
import os
from pathlib import Path
import secrets
import ssl
import sqlite3
import subprocess
import tempfile
import time
import urllib.error
import urllib.request
from mcp_harness import PROTOCOL, payload
from eval_contract import ROOT, check_order


def main():
    subprocess.run(["docker", "build", "-t", "business-data-mcp:smoke", "."], check=True)
    fixture_root = ROOT / "artifacts"
    fixture_root.mkdir(exist_ok=True)
    # Colima shares the home directory, not macOS /private/var/folders by default.
    with tempfile.TemporaryDirectory(prefix="docker-smoke-", dir=fixture_root) as directory:
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

            def container_url():
                binding = subprocess.check_output(
                    ["docker", "port", container, "8080/tcp"], text=True
                ).strip()
                assert binding.startswith("127.0.0.1:"), "fixture port must bind loopback only"
                return "https://" + binding

            url = container_url()
            for attempt in range(100):
                try:
                    with urllib.request.urlopen(url + "/health", timeout=1, context=context) as response:
                        assert json.load(response)["status"] == "ok"
                    break
                except OSError:
                    state = json.loads(subprocess.check_output(["docker", "inspect", container], text=True))[
                        0
                    ]["State"]
                    assert state["Running"], "owned container exited before readiness"
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

            def audit_snapshot(name):
                snapshot = Path(directory) / name
                snapshot.mkdir()
                subprocess.run(
                    ["docker", "cp", container + ":/data/.", str(snapshot)],
                    check=True,
                    stdout=subprocess.DEVNULL,
                )
                with sqlite3.connect(snapshot / "business.db") as connection:
                    assert connection.execute(
                        "SELECT total_cents,status FROM orders WHERE id='ORD-1001'"
                    ).fetchone() == (12990, "delivered")
                    return {row[0] for row in connection.execute("SELECT id FROM audit")}

            before_restart = audit_snapshot("before-restart")
            assert before_restart, "authenticated read must have durable audit state"
            subprocess.run(["docker", "restart", container], check=True, stdout=subprocess.DEVNULL)
            # Docker may allocate a new ephemeral published port when restarting.
            previous_url = url
            url = container_url()
            print(
                json.dumps(
                    {
                        "phase": "after_restart",
                        "previousUrl": previous_url,
                        "currentUrl": url,
                        "restartPublishedPortChanged": url != previous_url,
                    }
                ),
                flush=True,
            )
            for attempt in range(100):
                try:
                    with request(key) as response:
                        assert response.status == 200
                    break
                except OSError:
                    if attempt == 99:
                        raise
                    time.sleep(0.1)
            after_restart = audit_snapshot("after-restart")
            assert before_restart < after_restart, (
                "restart must preserve prior audit rows and append new calls"
            )
            image_config = subprocess.check_output(
                ["docker", "image", "inspect", "business-data-mcp:smoke"], text=True
            )
            assert key not in image_config, "runtime credential must not enter image configuration"
            assert key not in subprocess.check_output(
                ["docker", "history", "--no-trunc", "business-data-mcp:smoke"], text=True
            )
            assert key not in subprocess.check_output(
                ["docker", "logs", container], stderr=subprocess.STDOUT, text=True
            )
        except BaseException:
            # Retain useful safe startup evidence before removing only our own container.
            inspected = subprocess.run(["docker", "inspect", container], capture_output=True, text=True)
            if inspected.returncode == 0:
                state = json.loads(inspected.stdout)[0]["State"]
                logs = subprocess.run(["docker", "logs", container], capture_output=True, text=True)
                codes = []
                for line in (logs.stdout + logs.stderr).splitlines():
                    try:
                        record = json.loads(line)
                    except ValueError:
                        continue
                    if record.get("code") in {"configuration_error", "startup_error"}:
                        codes.append(record["code"])
                print(
                    json.dumps(
                        {
                            "dockerFailureState": state["Status"],
                            "exitCode": state["ExitCode"],
                            "oomKilled": state["OOMKilled"],
                            "safeStartupCodes": codes,
                        }
                    ),
                    flush=True,
                )
            raise
        finally:
            cleanup = subprocess.run(
                ["docker", "rm", "--force", "--volumes", container],
                capture_output=True,
                text=True,
            )
            if cleanup.returncode != 0:
                # Do not replace the primary failure with a secondary docker-stop exception.
                import sys

                if sys.exc_info()[0] is None:
                    raise RuntimeError("owned_container_cleanup_failed")
                print("owned_container_cleanup_failed", flush=True)
    print("PASS: Docker build, non-root runtime, health, auth and real MCP read")


if __name__ == "__main__":
    main()
