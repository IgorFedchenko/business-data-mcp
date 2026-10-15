"""Capture-only pre-dispatch guard and owned-process cleanup; no model client."""

import json
from contextlib import closing
import os
from pathlib import Path
import signal
import sqlite3
import subprocess
import threading
import time

ALLOWED_READS = frozenset({"get_order", "get_customer_history"})
MAX_CALLS = 8


class RunBudget:
    """One atomic lifetime budget per capture, shared across adapter restarts."""

    def __init__(self, path):
        self.path = Path(path)
        with closing(sqlite3.connect(self.path)) as db, db:
            db.execute(
                "CREATE TABLE IF NOT EXISTS budget (id INTEGER PRIMARY KEY CHECK(id=1), used INTEGER NOT NULL, failure TEXT)"
            )
            db.execute("INSERT OR IGNORE INTO budget VALUES (1,0,NULL)")
        self.path.chmod(0o600)

    def claim(self):
        # No wall clock or minute window: a restart/clock change never replenishes this run.
        with closing(sqlite3.connect(self.path, timeout=5)) as db, db:
            db.execute("BEGIN IMMEDIATE")
            used, failure = db.execute("SELECT used,failure FROM budget WHERE id=1").fetchone()
            if failure or used >= MAX_CALLS:
                db.execute("UPDATE budget SET failure=COALESCE(failure,'demo_budget_exceeded') WHERE id=1")
                return False
            db.execute("UPDATE budget SET used=used+1 WHERE id=1")
            return True

    def fail(self, code):
        assert code in {"demo_tool_forbidden", "demo_protocol_invalid", "demo_forward_failed"}
        with closing(sqlite3.connect(self.path)) as db, db:
            db.execute("UPDATE budget SET failure=COALESCE(failure,?) WHERE id=1", (code,))

    def state(self):
        with closing(sqlite3.connect(self.path)) as db, db:
            return db.execute("SELECT used,failure FROM budget WHERE id=1").fetchone()


class DispatchGuard:
    """The actual adapter forwarding path, also driven by scripted concurrent tests."""

    def __init__(self, budget, child_input):
        self.budget = budget
        self.child_input = child_input
        self.lock = threading.Lock()

    def forward(self, message):
        # Lock includes claim and pipe write. Concurrent attempts cannot exceed the cap.
        with self.lock:
            if message.get("method") == "tools/call":
                if message.get("params", {}).get("name") not in ALLOWED_READS:
                    self.budget.fail("demo_tool_forbidden")
                    return self.denied(message, "demo_tool_forbidden")
                if not self.budget.claim():
                    return self.denied(message, "demo_budget_exceeded")
            try:
                self.child_input.write(json.dumps(message) + "\n")
                self.child_input.flush()
            except (OSError, ValueError):
                self.budget.fail("demo_forward_failed")
                raise RuntimeError("demo_forward_failed") from None
        return None

    @staticmethod
    def denied(message, code):
        return {
            "jsonrpc": "2.0",
            "id": message.get("id"),
            "result": {"isError": True, "content": [{"type": "text", "text": json.dumps({"code": code})}]},
        }


def require_owned_group(group_file):
    """Fail closed if a client detached the adapter from the capture-owned session."""
    deadline = time.monotonic() + 2
    path = Path(group_file)
    while not path.exists():
        if time.monotonic() >= deadline:
            raise RuntimeError("capture_group_unavailable")
        time.sleep(0.01)
    expected = int(path.read_text())
    if os.getpgrp() != expected:
        raise RuntimeError("capture_group_mismatch")


def publish_owned_group(path, pid):
    target = Path(path)
    pending = target.with_suffix(".pending")
    pending.write_text(str(pid))
    pending.replace(target)


def stop_owned_group(process):
    """Popen must have start_new_session=True. Kill its group even if root exited."""
    try:
        os.killpg(process.pid, signal.SIGTERM)
    except ProcessLookupError:
        pass
    try:
        process.wait(timeout=1)
    except subprocess.TimeoutExpired:
        pass
    # Descendants may survive root exit or ignore TERM; always address the owned group.
    try:
        os.killpg(process.pid, signal.SIGKILL)
    except ProcessLookupError:
        pass
    process.wait(timeout=2)
