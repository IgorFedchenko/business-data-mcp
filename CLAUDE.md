# Business Data MCP

This is an independent MIT reference implementation. Use only synthetic data. Keep the
repository private until Igor reviews and explicitly approves publication. Never add
employer/client identifiers, contact details, API credentials, or production datasets.

Target .NET 8 and the official ModelContextProtocol SDK. Keep local stdio read-only by
default. HTTP requires separate read/write API keys. An MCP caller must never be able
to approve its own return intent; approval belongs to the interactive operator CLI.

On Studio, run builds and tests through `~/.local/bin/studio-heavy-run -- <command>`.
Use `./scripts/verify.sh` for the acceptance suite. Keep all credentials and runtime
SQLite/audit data in ignored files. Do not call any paid service in tests or CI.

## Repository structure

`src/BusinessData.Core` owns SQLite queries, domain rules, atomic return/idempotency
state and durable audit. `src/BusinessData.Server` owns official SDK tools, transport,
authentication, policy, telemetry and operator CLI. `tests/BusinessData.Tests` uses
xUnit, official in-memory SDK clients and TestServer. `tests/BusinessData.CrashFixture`
is test-only process composition: production projects must never reference it.
`scripts` contains independent raw JSON-RPC process checks, build and verification;
`evals` contains explicitly scripted fixtures and an independently frozen oracle.
Read `docs/architecture.md`, `docs/security.md` and `docs/testing.md` for boundaries.

## Build and verify

Install SDK 8.0.426; use committed NuGet locks. `./scripts/build.sh` builds and
`./scripts/run.sh --transport stdio` starts read-only. `./scripts/verify.sh` owns its
single Studio heavy ticket; never wrap it again. For focused inner commands use
`studio-heavy-run -- bash -c 'source scripts/environment.sh; <command>'` on Studio.
Cold-start is special: run `python3 scripts/cold-start.py` outside an outer ticket;
its copied build.sh acquires compilation's ticket and releases it before protocol IO.
Python 3.11+, pinned requirements-dev.lock, Unix PTY, Docker, OpenSSL and gitleaks
are required for the full local suite. Missing prerequisites are failures, not passes.

## Change rules

Preserve existing Git history, dates and agent attribution. Keep internal process,
review logs and portfolio text outside this repository. Repository visibility requires owner approval after the overall review.
GitHub Actions remains disabled after publication; do not enable or dispatch it without
separate owner authorization. New commits/pushes use `[skip ci]`.
Public-only workflows are dormant configuration and must not imply hosted green CI.
No external service/model keys, live business integrations or paid test services.
Default evaluation is deterministic scripted MCP contract verification, never a model
quality benchmark. Optional `--require-capture` must reject missing/forged recordings.
Never invent captured evidence or silently regenerate expected facts from outputs.
No production environment, HTTP or MCP fault injection switches; use test composition.
Keep audit/log/trace data redacted, stdout JSON-RPC only, permissions enforced on direct
calls as well as discovery, and human return approval outside MCP.
