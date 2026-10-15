#!/usr/bin/env python3
"""Independent evaluator controls; authored data here is never a model recording."""

import copy
import json
from pathlib import Path
import subprocess
import sys
import tempfile

from eval_contract import ROOT, check_answer


def validate(path):
    try:
        check_answer(json.loads(Path(path).read_text()))
    except Exception:
        raise SystemExit("answer_contract_rejected") from None


def main():
    if len(sys.argv) == 3 and sys.argv[1] == "--validate":
        validate(sys.argv[2])
        return
    original = json.loads((ROOT / "evals/fixtures/scripted-answer.json").read_text())["answer"]
    with tempfile.TemporaryDirectory() as directory:
        path = Path(directory) / "answer.json"

        def run(value, valid, case):
            path.write_text(value if isinstance(value, str) else json.dumps(value))
            result = subprocess.run(
                [sys.executable, __file__, "--validate", str(path)],
                text=True,
                capture_output=True,
                timeout=10,
            )
            assert (result.returncode == 0) is valid, case
            if not valid:
                assert result.stderr.strip() == "answer_contract_rejected", case

        run(original, True, "baseline")
        for case in ("amount", "status", "foreign_history", "missing", "extra", "unknown"):
            value = copy.deepcopy(original)
            if case == "amount":
                value["orders"][0]["totalCents"] = 12989
            elif case == "status":
                value["orders"][0]["status"] = "cancelled"
            elif case == "foreign_history":
                value["orders"][1]["id"] = "ORD-1003"
            elif case == "missing":
                del value["customerName"]
            elif case == "extra":
                value["authorizedWrite"] = True
            else:
                value["orders"][0]["id"] = "ORD-9999"
            run(value, False, case)
        run('{"malformed":', False, "malformed_json")
    print(
        "PASS: authored baseline; 7 independent subprocess schema/fact controls rejected by named assertion"
    )


if __name__ == "__main__":
    main()
