#!/usr/bin/env python3
"""Offline deterministic MCP replay. Scripted fixtures are never labelled model captures."""

import argparse
import copy
import json
from pathlib import Path
import tempfile
import sqlite3
import time
from eval_contract import ROOT, ORDERS, check_answer, check_capture, check_order
from mcp_harness import Server, error_code, payload


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument(
        "--assembly", default="src/BusinessData.Server/bin/Release/net8.0/BusinessData.Server.dll"
    )
    mode = parser.add_mutually_exclusive_group()
    mode.add_argument(
        "--require-capture",
        dest="scripted_only",
        action="store_false",
        help="Full acceptance: genuine recorded replay is mandatory (default)",
    )
    mode.add_argument(
        "--scripted-only",
        dest="scripted_only",
        action="store_true",
        help="Incomplete development run; cannot satisfy full acceptance",
    )
    parser.set_defaults(scripted_only=False)
    parser.add_argument("--capture", type=Path, default=ROOT / "docs/demo-transcript.json")
    args = parser.parse_args()
    # Fail before starting any host if a required artifact is absent or invalid.
    trace = None
    if not args.scripted_only:
        if not args.capture.is_file():
            raise SystemExit(
                "missing_recorded_capture: full acceptance requires a genuine Claude recording; no inference is run"
            )
        trace = json.loads(args.capture.read_text())
        check_capture(trace)
    start = time.monotonic()
    corpus = json.loads((ROOT / "evals/scenarios.json").read_text())
    scenarios = corpus["cases"]
    passed = 0
    with tempfile.TemporaryDirectory() as fixture:
        with Server(args.assembly, Path(fixture) / "eval.db") as server:
            for case in scenarios:
                result = server.call(case["tool"], case["args"])
                if "error" in case:
                    assert error_code(result) == case["error"], case["id"]
                else:
                    actual = payload(result)
                    if "oracleOrder" in case:
                        check_order(actual)
                        assert actual["id"] == case["oracleOrder"]
                    elif "orderIds" in case:
                        assert [order["id"] for order in actual] == case["orderIds"], case["id"]
                        for order in actual:
                            check_order(order)
                    elif "history" in case:
                        assert actual["customer"]["id"] == case["history"]
                        assert sorted(actual["orders"], key=lambda order: order["id"]) == [
                            order for order in ORDERS.values() if order["customerId"] == case["history"]
                        ]
                        assert actual["returns"] == []
                passed += 1
    # Explicitly scripted fixture setup; never represented as human approval evidence.
    write_passed = []
    with tempfile.TemporaryDirectory() as fixture:
        database = Path(fixture) / "write.db"
        with Server(args.assembly, database, writes=True) as server:
            prepare = {"orderId": "ORD-1001", "reason": "damaged", "idempotencyKey": "eval-prepare-0001"}
            intent = payload(server.call("prepare_return", prepare))["intent"]
            assert payload(server.call("prepare_return", prepare))["intent"]["id"] == intent["id"]
            write_passed.append("prepare-retry")
            assert (
                error_code(server.call("prepare_return", {**prepare, "reason": "wrong_item"}))
                == "idempotency_conflict"
            )
            write_passed.append("prepare-conflict")
            submit = {"intentId": intent["id"], "idempotencyKey": "eval-submit-0001"}
            assert error_code(server.call("request_return", submit)) == "confirmation_required"
            write_passed.append("approval-required")
            with sqlite3.connect(database) as connection:
                connection.execute(
                    "UPDATE return_intents SET actor='scripted:other' WHERE id=?", (intent["id"],)
                )
            assert error_code(server.call("request_return", submit)) == "forbidden"
            write_passed.append("cross-actor")
            with sqlite3.connect(database) as connection:
                connection.execute(
                    "UPDATE return_intents SET actor='stdio:local',expires_at='2000-01-01T00:00:00+00:00' WHERE id=?",
                    (intent["id"],),
                )
            assert error_code(server.call("request_return", submit)) == "expired_confirmation"
            write_passed.append("expired-intent")
            with sqlite3.connect(database) as connection:
                connection.execute(
                    "UPDATE return_intents SET expires_at='2099-01-01T00:00:00+00:00',approved_at='2030-01-01T00:00:00+00:00' WHERE id=?",
                    (intent["id"],),
                )
            ticket = payload(server.call("request_return", submit))
            assert ticket["intentId"] == intent["id"] and ticket["orderId"] == "ORD-1001"
            assert payload(server.call("request_return", submit)) == ticket
            with sqlite3.connect(database) as connection:
                assert connection.execute("SELECT id,intent_id,order_id FROM returns").fetchall() == [
                    (ticket["id"], intent["id"], "ORD-1001")
                ]
            write_passed.append("submit-retry")
    assert write_passed == corpus["writeCases"], "all declared write scenarios must execute"
    scripted = json.loads((ROOT / "evals/fixtures/scripted-answer.json").read_text())
    assert scripted["provenance"].startswith("scripted:")
    check_answer(scripted["answer"])
    # Negative controls prove both schema and facts are active, without modifying the oracle.
    for mutation in ("fact", "status", "foreign", "missing", "extra", "unknown"):
        bad = copy.deepcopy(scripted["answer"])
        if mutation == "fact":
            bad["orders"][0]["totalCents"] += 1
        elif mutation == "status":
            bad["orders"][0]["status"] = "shipped"
        elif mutation == "foreign":
            bad["customerId"] = "CUST-002"
        elif mutation == "missing":
            del bad["orders"][0]["currency"]
        elif mutation == "unknown":
            bad["orders"][0]["id"] = "ORD-9999"
        else:
            bad["unsupportedClaim"] = True
        try:
            check_answer(bad)
        except Exception:
            pass
        else:
            raise AssertionError("negative answer control was accepted")
    captured = False
    if trace is not None:
        for mutation in ("hash", "budget", "provenance"):
            bad_trace = copy.deepcopy(trace)
            if mutation == "hash":
                bad_trace["answerSha256"] = "0" * 64
            elif mutation == "budget":
                bad_trace["metrics"]["toolCalls"] = 9
            else:
                bad_trace["provenance"] = "scripted"
            try:
                check_capture(bad_trace)
            except Exception:
                pass
            else:
                raise AssertionError("capture negative control accepted: " + mutation)
        captured = True
    print(
        json.dumps(
            {
                "toolAssertionsPassed": passed + len(write_passed),
                "toolAssertionsTotal": len(scenarios) + len(write_passed),
                "toolPassRate": passed / len(scenarios),
                "writeBoundaryViolations": 0,
                "scriptedWriteCases": write_passed,
                "scriptedAnswerValid": True,
                "negativeControlsRejected": 6,
                "capturedAnswerReplayed": captured,
                "acceptanceComplete": not args.scripted_only,
                "mode": "scripted_only_incomplete" if args.scripted_only else "strict_acceptance",
                "elapsedSeconds": round(time.monotonic() - start, 3),
                "modelTokens": None,
                "modelCost": None,
                "usageReason": "offline scripted replay; no inference measured",
            }
        )
    )


if __name__ == "__main__":
    main()
