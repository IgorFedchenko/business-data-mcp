#!/usr/bin/env python3
"""Local capture-only stdio budget adapter; not a public server feature."""

import argparse
import json
import subprocess
import sys
import threading
from capture_guard import DispatchGuard, RunBudget, require_owned_group


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--budget-file", required=True)
    parser.add_argument("--owner-group-file", required=True)
    parser.add_argument("command", nargs=argparse.REMAINDER)
    args = parser.parse_args()
    require_owned_group(args.owner_group_file)
    command = args.command[1:] if args.command[:1] == ["--"] else args.command
    if not command:
        raise SystemExit("capture_server_command_missing")
    budget = RunBudget(args.budget_file)
    # No detached session: server and adapter must stay in the recorder-owned group.
    child = subprocess.Popen(
        command, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, text=True
    )
    output_lock = threading.Lock()

    def emit(message):
        with output_lock:
            sys.stdout.write(message + "\n")
            sys.stdout.flush()

    def read_child():
        for line in child.stdout:
            emit(line.rstrip("\n"))

    reader = threading.Thread(target=read_child, daemon=True)
    reader.start()
    guard = DispatchGuard(budget, child.stdin)
    try:
        while True:
            line = sys.stdin.readline(65538)
            if not line:
                break
            if len(line) > 65536:
                budget.fail("demo_protocol_invalid")
                return 2
            try:
                message = json.loads(line)
                if not isinstance(message, dict):
                    raise ValueError()
                denied = guard.forward(message)
            except (ValueError, TypeError, AttributeError):
                budget.fail("demo_protocol_invalid")
                return 2
            if denied is not None:
                emit(json.dumps(denied))
                return 2
        return 0
    finally:
        child.stdin.close()
        try:
            child.wait(timeout=0.5)
        except subprocess.TimeoutExpired:
            child.terminate()
            try:
                child.wait(timeout=0.5)
            except subprocess.TimeoutExpired:
                child.kill()
                child.wait(timeout=2)
        reader.join(timeout=0.5)
        child.stdout.close()


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except RuntimeError:
        raise SystemExit("capture_adapter_failed") from None
