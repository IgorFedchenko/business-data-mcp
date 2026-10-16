# 4. Evaluate deterministic facts and boundaries without model billing

Status: accepted.

The server calls no model. Offline evaluation replays explicitly scripted tool choices
against real processes and checks results against a separate frozen factual oracle.
A strict JSON schema rejects missing/unknown fields; exact facts reject hallucinations.
Negative fixtures prove the evaluator rejects corrupted answers.

A real Claude capture is a separate provenance category, never filled by an authored
fixture. Preserve its original answer and hashes, prompt version, actual calls/audit,
observed usage and elapsed time. Missing cost/usage is null with a reason. There is no
LLM-as-judge release gate or live inference in CI.

Read-only assistant recording and deterministic write/operator demonstration have
separate attribution. Scripted adversarial strings establish protocol/policy behavior,
not universal prompt-injection resistance. ActivitySource/Meter provide local telemetry;
Langfuse/Phoenix and hosted exporters are unnecessary for this deterministic service.

Local acceptance defaults to deterministic scripted MCP contract evaluation with a frozen
oracle. Genuine Claude capture/GIF is optional. Explicit `--require-capture` remains strict
and rejects missing or forged evidence; scripted results make no model-quality claim.
