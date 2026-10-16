#!/usr/bin/env python3
"""Hard-crash proof using explicitly TEST-ONLY SDK composition and independent SQLite."""

import json
from pathlib import Path
import re
import sqlite3
import tempfile
import time

from mcp_harness import Server, payload

PRODUCTION = "src/BusinessData.Server/bin/Release/net8.0/BusinessData.Server.dll"
FIXTURE = "tests/BusinessData.CrashFixture/bin/Release/net8.0/BusinessData.CrashFixture.dll"


def main():
    assert "BusinessData.CrashFixture" not in Path(PRODUCTION).with_suffix(".deps.json").read_text()
    with tempfile.TemporaryDirectory(prefix="business-hard-crash-") as directory:
        for tool in ("prepare_return", "request_return"):
            for phase in ("before_execution", "after_business"):
                case = Path(directory) / (tool + "-" + phase)
                case.mkdir()
                db, marker = case / "data.db", case / "barrier.json"
                prepare = {"orderId": "ORD-1001", "reason": "damaged", "idempotencyKey": "crash-prepare-key"}
                intent_id = None
                if tool == "request_return":
                    with Server(PRODUCTION, db, writes=True) as setup:
                        intent_id = payload(setup.call("prepare_return", prepare))["intent"]["id"]
                        setup.approve(intent_id)
                arguments = (
                    prepare
                    if tool == "prepare_return"
                    else {"intentId": intent_id, "idempotencyKey": "crash-submit-key"}
                )
                command = ["dotnet", str(Path(FIXTURE).resolve()), str(db), str(marker), phase, tool]
                with Server(FIXTURE, db, writes=True, command=command) as fixture:
                    call_id = 9001
                    message = {
                        "jsonrpc": "2.0",
                        "id": call_id,
                        "method": "tools/call",
                        "params": {"name": tool, "arguments": arguments},
                    }
                    fixture.process.stdin.write(json.dumps(message) + "\n")
                    fixture.process.stdin.flush()
                    deadline = time.monotonic() + 15
                    while not marker.exists():
                        assert fixture.process.poll() is None, "test host exited before barrier"
                        assert time.monotonic() < deadline, "test-only durable barrier not reached"
                        time.sleep(0.02)
                    barrier = json.loads(marker.read_text())
                    assert barrier["phase"] == phase and barrier["tool"] == tool
                    assert re.fullmatch(r"[a-f0-9]{32}", barrier["correlation"])
                    with sqlite3.connect(db) as connection:
                        audit = connection.execute(
                            "SELECT tool,outcome,result_code FROM audit WHERE id=?", (barrier["correlation"],)
                        ).fetchone()
                        assert audit == (tool, "started", None)
                        intents = connection.execute(
                            "SELECT id,actor,order_id,reason,consumed_at FROM return_intents"
                        ).fetchall()
                        tickets = connection.execute(
                            "SELECT id,intent_id,order_id,actor FROM returns"
                        ).fetchall()
                    committed = phase == "after_business"
                    if tool == "prepare_return":
                        assert len(intents) == int(committed) and tickets == []
                        if committed:
                            intent_id = intents[0][0]
                            assert intents[0][1:] == ("stdio:local", "ORD-1001", "damaged", None)
                    else:
                        assert len(intents) == 1 and intents[0][0] == intent_id
                        assert (intents[0][4] is not None) == committed
                        assert len(tickets) == int(committed)
                        if committed:
                            assert tickets[0][1:] == (intent_id, "ORD-1001", "stdio:local")
                    # No response delivery is promised or invented: process is killed inside the barrier.
                    while not fixture.responses.empty():
                        assert fixture.responses.get_nowait().get("id") != call_id
                    fixture.process.kill()
                    assert fixture.process.wait(timeout=10) != 0
                with Server(PRODUCTION, db, writes=True) as recovered:
                    response = payload(recovered.call(tool, arguments))
                    replay = payload(recovered.call(tool, arguments))
                    assert response == replay
                    if tool == "prepare_return":
                        actual_id = response["intent"]["id"]
                        if committed:
                            assert actual_id == intent_id
                        with sqlite3.connect(db) as connection:
                            assert connection.execute("SELECT id FROM return_intents").fetchall() == [
                                (actual_id,)
                            ]
                    else:
                        assert response["intentId"] == intent_id and response["orderId"] == "ORD-1001"
                        if committed:
                            assert response["id"] == tickets[0][0]
                        with sqlite3.connect(db) as connection:
                            assert connection.execute("SELECT id,intent_id FROM returns").fetchall() == [
                                (response["id"], intent_id)
                            ]
                print(
                    "PASS test-only hard crash:",
                    tool,
                    phase,
                    "durable started + production restart/same-key recovery",
                )


if __name__ == "__main__":
    main()
