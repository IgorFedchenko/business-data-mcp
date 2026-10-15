#!/usr/bin/env python3
"""Actual stdio/Kestrel/interactive PTY acceptance; synthetic data, no LLM."""

import argparse
import datetime as dt
import json
from pathlib import Path
import re
import secrets
import sqlite3
import tempfile
from eval_contract import check_order
from mcp_harness import Server, error_code, payload


def utc(value):
    parsed = dt.datetime.fromisoformat(value.replace("Z", "+00:00"))
    assert parsed.utcoffset() == dt.timedelta(0), "timestamp must be UTC"
    return parsed


def check_diagnostics(server, forbidden=()):
    for line in server.stderr:
        entry = json.loads(line)
        assert isinstance(entry, dict), "diagnostic must be a JSON object"
    text = "".join(server.stderr)
    for canary in forbidden:
        assert canary not in text, "private argument leaked to diagnostic"


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument(
        "--assembly", default="src/BusinessData.Server/bin/Release/net8.0/BusinessData.Server.dll"
    )
    args = parser.parse_args()
    with tempfile.TemporaryDirectory(prefix="business-acceptance-") as fixture:
        db = Path(fixture) / "stdio.db"
        with Server(args.assembly, db) as reader:
            tools = reader.send("tools/list", {})["tools"]
            assert len(tools) == 4 and all(t["annotations"]["readOnlyHint"] for t in tools)
            assert all("outputSchema" in tool for tool in tools), "typed output schemas required"
            assert all(tool.get("description") and tool["inputSchema"]["type"] == "object" for tool in tools)
            assert {tool["name"] for tool in tools} == {
                "get_order",
                "get_order_status",
                "get_customer_history",
                "search_orders",
            }
            for order_id in ("ORD-1001", "ORD-1002", "ORD-1003", "ORD-1004"):
                check_order(payload(reader.call("get_order", {"orderId": order_id})))
            assert (
                error_code(
                    reader.call(
                        "request_return",
                        {"intentId": "RI-" + "a" * 32, "idempotencyKey": "direct-submit-denial"},
                    )
                )
                == "forbidden"
            )
            assert error_code(reader.call("approve_return", {"intentId": "RI-" + "a" * 32})) == "unknown_tool"
            assert (
                error_code(
                    reader.call(
                        "prepare_return",
                        {"orderId": "ORD-1001", "reason": "damaged", "idempotencyKey": "prepare-process-01"},
                    )
                )
                == "forbidden"
            )
        check_diagnostics(reader)
        prepare = {"orderId": "ORD-1001", "reason": "damaged", "idempotencyKey": "prepare-process-01"}
        before = dt.datetime.now(dt.timezone.utc)
        with Server(args.assembly, db, writes=True) as writer:
            writer_tools = writer.send("tools/list", {})["tools"]
            schemas = {
                "get_order": ({"orderId"}, {"orderId"}),
                "get_order_status": ({"orderId"}, {"orderId"}),
                "get_customer_history": ({"customerId"}, {"customerId"}),
                "search_orders": ({"query", "limit"}, {"query"}),
                "prepare_return": (
                    {"orderId", "reason", "idempotencyKey"},
                    {"orderId", "reason", "idempotencyKey"},
                ),
                "request_return": ({"intentId", "idempotencyKey"}, {"intentId", "idempotencyKey"}),
            }
            assert {tool["name"] for tool in writer_tools} == set(schemas)
            for tool in writer_tools:
                properties, required = schemas[tool["name"]]
                assert tool.get("description") and tool.get("outputSchema")
                assert set(tool["inputSchema"]["properties"]) == properties
                assert set(tool["inputSchema"]["required"]) == required
                assert all(value.get("description") for value in tool["inputSchema"]["properties"].values())
            intent = payload(writer.call("prepare_return", prepare))["intent"]
            assert re.fullmatch(r"RI-[a-f0-9]{32}", intent["id"])
            assert intent["actor"] == "stdio:local" and intent["orderId"] == "ORD-1001"
            after = dt.datetime.now(dt.timezone.utc)
            assert (
                before + dt.timedelta(minutes=10)
                <= utc(intent["expiresAt"])
                <= after + dt.timedelta(minutes=10)
            )
            assert payload(writer.call("prepare_return", prepare))["intent"]["id"] == intent["id"]
            changed = {**prepare, "reason": "wrong_item"}
            assert error_code(writer.call("prepare_return", changed)) == "idempotency_conflict"
            submit = {"intentId": intent["id"], "idempotencyKey": "submit-process-01"}
            assert error_code(writer.call("request_return", submit)) == "confirmation_required"
            # Piped approval is rejected by the real adapter before state mutation.
            import subprocess

            piped = subprocess.run(
                ["dotnet", writer.assembly, "approve-return", intent["id"]],
                input=intent["id"] + "\n",
                capture_output=True,
                text=True,
                env=writer.env,
            )
            assert piped.returncode == 2
            for invalid_confirmation in ("no", "\x04", intent["id"][:12], "RI-" + "0" * 32):
                writer.approve(intent["id"], invalid_confirmation, expected_exit=2)
                with sqlite3.connect(db) as connection:
                    assert connection.execute(
                        "SELECT approved_at,consumed_at FROM return_intents WHERE id=?", (intent["id"],)
                    ).fetchone() == (None, None), "rejected CLI input changed approval"
            writer.approve(intent["id"])
            ticket = payload(writer.call("request_return", submit))
            assert re.fullmatch(r"RET-[a-f0-9]{32}", ticket["id"])
            assert ticket["intentId"] == intent["id"] and ticket["orderId"] == "ORD-1001"
            assert before <= utc(ticket["createdAt"]) <= dt.datetime.now(dt.timezone.utc)
            # Production host crash after a confirmed durable commit, rather than graceful shutdown.
            # In-flight pre/post-commit interruption needs separate test-host barriers.
            writer.process.kill()
            assert writer.process.wait(timeout=10) != 0
        check_diagnostics(writer, ("prepare-process-01", "submit-process-01"))
        # New host and connection; replay is durable, not a process cache.
        with Server(args.assembly, db, writes=True) as restarted:
            assert payload(restarted.call("request_return", submit)) == ticket
            expired = payload(
                restarted.call(
                    "prepare_return",
                    {"orderId": "ORD-1003", "reason": "damaged", "idempotencyKey": "expired-process-01"},
                )
            )["intent"]
            # Explicit synthetic fixture setup avoids sleeping ten minutes.
            with sqlite3.connect(db) as connection:
                connection.execute(
                    "UPDATE return_intents SET expires_at=? WHERE id=?",
                    ("2000-01-01T00:00:00.0000000+00:00", expired["id"]),
                )
            restarted.approve(expired["id"], expected_exit=1)
            with sqlite3.connect(db) as connection:
                assert connection.execute(
                    "SELECT approved_at FROM return_intents WHERE id=?", (expired["id"],)
                ).fetchone() == (None,)
            assert (
                error_code(
                    restarted.call(
                        "request_return", {"intentId": expired["id"], "idempotencyKey": "expired-submit-01"}
                    )
                )
                == "expired_confirmation"
            )
        with sqlite3.connect(db) as connection:
            assert connection.execute("SELECT id,order_id,intent_id FROM returns").fetchall() == [
                (ticket["id"], "ORD-1001", intent["id"])
            ]
            assert (
                connection.execute(
                    "SELECT approved_at,consumed_at FROM return_intents WHERE id=?", (intent["id"],)
                ).fetchone()[1]
                is not None
            )
            audit = connection.execute(
                "SELECT outcome,result_code,arguments,result_metadata FROM audit"
            ).fetchall()
            assert all(row[0] != "started" for row in audit)
            assert any(row[1] == "confirmation_required" for row in audit)
            serialized = json.dumps(audit)
            assert "prepare-process-01" not in serialized and "submit-process-01" not in serialized
            assert ticket["id"] in serialized, "safe result identity must survive audit"
        # Deterministic production crash before audit persistence: an independent write
        # transaction prevents both audit-start and business commits. This does not
        # pretend to be an in-flight post-business/pre-audit-completion barrier.
        crash_db = Path(fixture) / "before-audit-crash.db"
        crash_prepare = {"orderId": "ORD-1001", "reason": "damaged", "idempotencyKey": "crash-prepare-01"}
        with Server(args.assembly, crash_db, writes=True) as blocked:
            with sqlite3.connect(crash_db) as lock:
                lock.execute("BEGIN IMMEDIATE")
                message = {
                    "jsonrpc": "2.0",
                    "id": 9000,
                    "method": "tools/call",
                    "params": {"name": "prepare_return", "arguments": crash_prepare},
                }
                blocked.process.stdin.write(json.dumps(message) + "\n")
                blocked.process.stdin.flush()
                blocked.process.kill()
                assert blocked.process.wait(timeout=10) != 0
                assert lock.execute("SELECT count(*) FROM return_intents").fetchone()[0] == 0
                assert lock.execute("SELECT count(*) FROM returns").fetchone()[0] == 0
        with Server(args.assembly, crash_db, writes=True) as recovered:
            recovery = payload(recovered.call("prepare_return", crash_prepare))["intent"]
            assert payload(recovered.call("prepare_return", crash_prepare))["intent"]["id"] == recovery["id"]
        with sqlite3.connect(crash_db) as connection:
            assert connection.execute("SELECT id FROM return_intents").fetchall() == [(recovery["id"],)]
        keys = (secrets.token_hex(32), secrets.token_hex(32))
        with Server(args.assembly, Path(fixture) / "http.db", writes=True, http=True, keys=keys) as http:
            request = json.dumps({"jsonrpc": "2.0", "id": 100, "method": "tools/list", "params": {}}).encode()
            assert http.raw_http(request)[0] == 401
            assert http.raw_http(request, "invalid")[0] == 401
            assert http.raw_http(request, keys[0], {"Origin": "https://untrusted.example"})[0] == 403
            assert http.raw_http(request, keys[0], {"Host": "untrusted.example"})[0] == 400
            assert http.raw_http(b" " * 65537, keys[0])[0] == 413
            http.initialize(keys[0])
            http.initialize(keys[1])
            assert len(http.send("tools/list", {}, key=keys[0])["tools"]) == 4
            assert len(http.send("tools/list", {}, key=keys[1])["tools"]) == 6
            check_order(payload(http.call("get_order", {"orderId": "ORD-1001"}, keys[0])))
            assert error_code(http.call("prepare_return", prepare, keys[0])) == "forbidden"
            other = payload(http.call("prepare_return", prepare, keys[1]))["intent"]
            assert other["actor"] == "api:writer"
            assert (
                error_code(
                    http.call(
                        "request_return",
                        {"intentId": intent["id"], "idempotencyKey": "foreign-key-0001"},
                        keys[1],
                    )
                )
                == "not_found"
            )
        check_diagnostics(http, keys)
    print(
        "PASS: real stdio, Kestrel, PTY approval, denied writes, expiry fixture, restart replay, independent SQLite assertions"
    )


if __name__ == "__main__":
    main()
