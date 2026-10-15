# Verification tooling

Server launch and focused .NET tests need .NET SDK 8.0.426 and Git. The lightweight
stdio protocol smoke additionally needs Python 3; no LLM, Docker or PTY is needed.
Full verification needs Linux/macOS with Unix PTY support, Python 3.11+ (CI uses 3.12),
a running Docker daemon, OpenSSL and free gitleaks CLI 8.30.1.
For isolated Python tools: `python3 -m venv .venv`, then
`.venv/bin/python -m pip install -r requirements-dev.lock`. Optional pre-commit hooks use
the same fast config, Ruff and gitleaks commands; install pre-commit separately.

`./scripts/verify.sh` owns one Studio heavy-job ticket and calls
`scripts/verify-inner.sh`. Do not wrap the outer script a second time. On other hosts
it executes the inner sequence directly. Inner scripts never acquire another lock;
on Studio call them only beneath `studio-heavy-run`. No environment variable bypass
is provided. Formatting that compiles a workspace also needs the queue:

```bash
~/.local/bin/studio-heavy-run -- bash -c 'source scripts/environment.sh; dotnet format BusinessData.sln --no-restore'
```

The sequence is config parsing, Ruff lint/format, gitleaks current tree/history,
locked restore, C# format verification, warnings-as-errors build, unit/in-memory SDK/
TestServer SDK tests, real stdio/Kestrel JSON-RPC+PTY/restart acceptance, offline evals,
renamed template build/stdio, and Docker build/non-root health/auth/read smoke. A
missing tool or failed gate fails verification; no silent skips. Docker is required
for the complete acceptance entrypoint.

`process-acceptance.py` is an independent raw JSON-RPC Python client; production hosts
use the official SDK. In-memory and TestServer tests use actual official SDK clients.
`evaluate.py --require-capture` is the strict default used by verify/CI: missing or
invalid genuine recorded replay fails before starting a host. Capture remains owner-
authorized; CI never creates it. `evaluate.py --scripted-only` is an explicitly
incomplete development mode that runs only scripted scenarios. It cannot satisfy
full acceptance or demo readiness. The no-inference `check-demo-contracts.py` executes
a local argv stub and proves missing config/recording fail without invoking Claude.
`check-capture-guard.py` drives the actual capture adapter/guard with scripted Python
child processes, concurrent attempts, independent counts and group cleanup checks;
these are protocol/control fixtures, never Claude evidence.

Hosted private Actions remains disabled until free minutes and $0 stop are confirmed.
A skipped job is not green. CodeQL is public-only; premium private code/secret scanning
is not enabled. Gitleaks is the free standalone binary, not a license-gated action.


Focused entrypoints: `scripts/process-acceptance.py` is the
real-process e2e entry; `scripts/evaluate.py` is strict offline eval; Docker uses
`scripts/docker-smoke.py`; template uses `scripts/template-smoke-inner.sh` within the
single verification ticket. Focused mutation checks use separate Studio tickets.
