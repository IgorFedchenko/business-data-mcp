#!/usr/bin/env python3
"""Scripted no-inference budget/concurrency and owned-descendant cleanup checks."""

from concurrent.futures import ThreadPoolExecutor
import json
from pathlib import Path
import queue
import subprocess
import sys
import tempfile
import threading
import time
from unittest.mock import patch
from capture_guard import DispatchGuard, RunBudget, publish_owned_group, stop_owned_group

ROOT = Path(__file__).resolve().parents[1]


def wait_for(predicate, label, seconds=5):
    deadline = time.monotonic() + seconds
    while not predicate():
        assert time.monotonic() < deadline, label
        time.sleep(0.02)


def running(pid):
    result = subprocess.run(["ps", "-p", str(pid), "-o", "stat="], capture_output=True, text=True)
    return result.returncode == 0 and not result.stdout.strip().startswith("Z")


def call(number):
    return {
        "jsonrpc": "2.0",
        "id": number,
        "method": "tools/call",
        "params": {"name": "get_order", "arguments": {"orderId": "ORD-1001"}},
    }


def main():
    with tempfile.TemporaryDirectory(prefix="scripted-capture-guard-") as directory:
        fixture = Path(directory)
        backend = fixture / "counted_backend.py"
        backend.write_text("""import json,os,signal,subprocess,sys,time
from pathlib import Path
counter,pids=map(Path,sys.argv[1:])
child=subprocess.Popen([sys.executable,"-c","import time; time.sleep(60)"])
pids.write_text(json.dumps([os.getpid(),child.pid]))
def stop(*_):
    if child.poll() is None: child.terminate()
    child.wait(timeout=2)
    sys.exit(0)
signal.signal(signal.SIGTERM,stop)
for line in sys.stdin:
    request=json.loads(line)
    with counter.open("a") as file: file.write(str(request["id"])+"\\n")
    print(json.dumps({"jsonrpc":"2.0","id":request["id"],"result":{"scripted":True}}),flush=True)
while True: time.sleep(.1)
""")
        # Actual dispatch guard, nine concurrent callers, independently counted real child.
        counter, pids = fixture / "executed", fixture / "pids"
        child = subprocess.Popen(
            [sys.executable, str(backend), str(counter), str(pids)],
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            text=True,
            start_new_session=True,
        )
        budget = RunBudget(fixture / "lifetime.db")
        guard = DispatchGuard(budget, child.stdin)
        try:
            with ThreadPoolExecutor(max_workers=9) as pool:
                barrier = threading.Barrier(9)

                def attempt(number):
                    barrier.wait()
                    return guard.forward(call(number))

                results = list(pool.map(attempt, range(1, 10)))
            rejected = [item for item in results if item is not None]
            assert len(rejected) == 1, "ninth_must_be_denied"
            assert "demo_budget_exceeded" in json.dumps(rejected[0])
            wait_for(
                lambda: counter.exists() and len(counter.read_text().splitlines()) == 8,
                "exactly_eight_independent_child_executions",
            )
            before = counter.read_text()
            # Reconstruct guard/budget as if adapter restarted, then cross wall-clock windows.
            with patch("time.time", return_value=4102444800):
                restarted = DispatchGuard(RunBudget(fixture / "lifetime.db"), child.stdin)
                assert restarted.forward(call(10)) is not None, "clock_rollover_must_not_reset"
            assert counter.read_text() == before and budget.state() == (8, "demo_budget_exceeded")
            # Also cross the window BEFORE any excess attempt has latched a failure.
            fresh_path = fixture / "pre-rollover.db"
            fresh_guard = DispatchGuard(RunBudget(fresh_path), child.stdin)
            for number in range(11, 19):
                assert fresh_guard.forward(call(number)) is None
            wait_for(lambda: len(counter.read_text().splitlines()) == 16, "fresh_run_eight_executions")
            assert RunBudget(fresh_path).state() == (8, None)
            with patch("time.time", return_value=4102444800):
                new_adapter = DispatchGuard(RunBudget(fresh_path), child.stdin)
                assert new_adapter.forward(call(19)) is not None, "pre_failure_rollover_must_not_reset"
            assert len(counter.read_text().splitlines()) == 16
        finally:
            stop_owned_group(child)
            child.stdin.close()
            child.stdout.close()
        descendants = json.loads(pids.read_text())
        wait_for(lambda: not any(running(pid) for pid in descendants), "counted_fixture_cleanup")
        print(
            "PASS scripted: 9 concurrent dispatch attempts, exactly8 independently executed, ninth denied; restart/clock change cannot reset"
        )

        # Exercise actual proxy and same group cleanup used by recorder, both timeout and over-budget.
        for reason in ("timeout", "budget"):
            counter = fixture / (reason + "-executed")
            pids = fixture / (reason + "-pids")
            owner = fixture / (reason + "-owner")
            budget_path = fixture / (reason + "-budget.db")
            RunBudget(budget_path)
            proxy = subprocess.Popen(
                [
                    sys.executable,
                    str(ROOT / "scripts/capture-stdio-proxy.py"),
                    "--budget-file",
                    str(budget_path),
                    "--owner-group-file",
                    str(owner),
                    "--",
                    sys.executable,
                    str(backend),
                    str(counter),
                    str(pids),
                ],
                stdin=subprocess.PIPE,
                stdout=subprocess.PIPE,
                stderr=subprocess.DEVNULL,
                text=True,
                start_new_session=True,
            )
            publish_owned_group(owner, proxy.pid)
            responses = queue.Queue()

            def read(stream=proxy.stdout):
                for line in stream:
                    responses.put(json.loads(line))

            reader = threading.Thread(target=read, daemon=True)
            reader.start()
            try:
                wait_for(pids.exists, "owned_backend_descendant_started")
                if reason == "budget":
                    lock = threading.Lock()
                    barrier = threading.Barrier(9)

                    def send(number):
                        barrier.wait()
                        with lock:
                            proxy.stdin.write(json.dumps(call(number)) + "\n")
                            proxy.stdin.flush()

                    with ThreadPoolExecutor(max_workers=9) as pool:
                        list(pool.map(send, range(1, 10)))
                    wait_for(
                        lambda: RunBudget(budget_path).state()[1] == "demo_budget_exceeded",
                        "actual_proxy_guard_failure",
                    )
                    seen = []
                    deadline = time.monotonic() + 3
                    while not any("demo_budget_exceeded" in json.dumps(item) for item in seen):
                        seen.append(responses.get(timeout=max(0.01, deadline - time.monotonic())))
                else:
                    # A stalled client/server with live descendant reaches the artificial test deadline.
                    try:
                        proxy.wait(timeout=0.1)
                    except subprocess.TimeoutExpired:
                        pass
                    else:
                        raise AssertionError("stalled_fixture_exited_before_deadline")
            finally:
                stop_owned_group(proxy)
                proxy.stdin.close()
                reader.join(timeout=2)
                proxy.stdout.close()
            descendants = json.loads(pids.read_text())
            wait_for(
                lambda: not any(running(pid) for pid in [proxy.pid, *descendants]),
                reason + "_descendants_must_stop",
            )
            if reason == "budget":
                assert len(counter.read_text().splitlines()) <= 8, "proxy_must_not_execute_ninth"
            print("PASS scripted actual proxy: owned descendant cleanup after " + reason)

        orphan_pids = fixture / "exited-root-pids"
        root = subprocess.Popen(
            [
                sys.executable,
                "-c",
                "import json,os,subprocess,sys; from pathlib import Path; "
                "child=subprocess.Popen([sys.executable,'-c','import time; time.sleep(60)']); "
                "Path(sys.argv[1]).write_text(json.dumps([child.pid]))",
                str(orphan_pids),
            ],
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
            start_new_session=True,
        )
        try:
            assert root.wait(timeout=5) == 0
            descendants = json.loads(orphan_pids.read_text())
            assert all(running(pid) for pid in descendants), "exited_root_fixture_must_have_live_descendant"
        finally:
            stop_owned_group(root)
        wait_for(lambda: not any(running(pid) for pid in descendants), "cleanup_after_root_already_exited")
        print("PASS scripted: group cleanup removes descendant after owned root already exited")


if __name__ == "__main__":
    main()
