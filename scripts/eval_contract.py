"""Independent oracle and strict JSON-schema/fact checks shared by offline evaluation/capture."""

import json
from pathlib import Path
from jsonschema import Draft202012Validator

ROOT = Path(__file__).resolve().parents[1]
ORACLE = json.loads((ROOT / "evals/expected-facts.json").read_text())
ORDERS = {order["id"]: order for order in ORACLE["orders"]}
SCHEMA = json.loads((ROOT / "evals/answer.schema.json").read_text())


def check_order(actual):
    assert actual == ORDERS[actual["id"]], "order differs from independent expected facts"


def check_answer(answer):
    Draft202012Validator(SCHEMA).validate(answer)
    assert answer["customerId"] == "CUST-001"
    assert answer["customerName"] == "Avery Morgan"
    expected = [
        {key: order[key] for key in ("id", "status", "totalCents", "currency")}
        for order in ORACLE["orders"]
        if order["customerId"] == "CUST-001"
    ]
    assert sorted(answer["orders"], key=lambda order: order["id"]) == expected, (
        "answer facts differ from independent oracle"
    )


def check_capture(trace):
    import hashlib
    from datetime import datetime

    assert trace["provenance"] == "captured_claude", "capture_provenance"
    assert trace["data"] == "synthetic" and trace["transport"] == "stdio", "capture_scope"
    for text_field, hash_field in (("answer", "answerSha256"), ("prompt", "promptSha256")):
        assert hashlib.sha256(trace[text_field].encode()).hexdigest() == trace[hash_field], "capture_hash"
    assert trace["promptVersion"] == "read-support-v1", "prompt_version"
    assert trace["prompt"] == (ROOT / "evals/prompts/read-support-v1.txt").read_text(), "prompt_content"
    assert (
        trace["fixtureSha256"]
        == hashlib.sha256((ROOT / "evals/expected-facts.json").read_bytes()).hexdigest()
    ), "fixture_hash"
    assert datetime.fromisoformat(trace["capturedAt"]).utcoffset().total_seconds() == 0, "capture_utc"
    metrics = trace["metrics"]
    assert 1 <= metrics["assistantTurns"] <= 6, "turn_budget"
    assert 2 <= metrics["toolCalls"] <= 8, "tool_budget"
    assert metrics["dispatches"] == metrics["toolCalls"], "pre_dispatch_budget_evidence"
    assert 0 <= metrics["elapsedSeconds"] <= 120, "capture_deadline"
    calls = [event for event in trace["events"] if event["type"] == "tool_call"]
    assert len(calls) == metrics["toolCalls"], "tool_count"
    assert {call["name"] for call in calls} == {
        "mcp__business-data__get_order",
        "mcp__business-data__get_customer_history",
    }, "capture_allowlist"
    assert all(
        call["arguments"]
        == ({"orderId": "ORD-1001"} if call["name"].endswith("get_order") else {"customerId": "CUST-001"})
        for call in calls
    ), "capture_arguments"
    results = {event["id"]: event for event in trace["events"] if event["type"] == "tool_result"}
    assert set(results) == {call["id"] for call in calls}, "capture_result_grounding"
    for call in calls:
        content = results[call["id"]]["content"]
        if isinstance(content, list):
            content = next(block["text"] for block in content if block.get("type") == "text")
        result = json.loads(content)
        if call["name"].endswith("get_order"):
            check_order(result)
        else:
            assert result["customer"]["id"] == "CUST-001", "captured_history_customer"
            assert sorted(result["orders"], key=lambda order: order["id"]) == [
                ORDERS["ORD-1001"],
                ORDERS["ORD-1002"],
            ], "captured_history_facts"
    assert len(trace["serverAudit"]) == len(calls), "capture_audit_count"
    assert all(
        row["outcome"] == "success" and row["resultCode"] == "ok" and row["actor"] == "stdio:local"
        for row in trace["serverAudit"]
    ), "capture_audit_outcome"
    assert {row["tool"] for row in trace["serverAudit"]} == {"get_order", "get_customer_history"}, (
        "capture_audit_tools"
    )
    check_answer(json.loads(trace["answer"]))
