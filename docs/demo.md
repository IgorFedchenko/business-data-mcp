# Demo provenance and reproduction

The server makes no model calls. The genuine Claude recording/GIF is currently absent;
no scripted answer is presented as a recording. Capture requires owner confirmation
that included OAuth entitlement cannot incur additional charges. Authentication alone
is insufficient. No model call runs in CI.

Build and pass local scripted contract verification first. A recording/GIF is optional
and is not required for local acceptance. Only with existing OAuth entitlement and
Extra Usage disabled:

```bash
source scripts/environment.sh
python3 scripts/record-claude-demo.py --included-usage-confirmed
python3 scripts/evaluate.py --require-capture
python3 scripts/render-demo.py
```

Before an authorized capture, run `python3 scripts/check-demo-contracts.py` to validate
argv and the missing-record gate without authentication or inference. Also run
`python3 scripts/check-capture-guard.py`: its explicitly scripted child fixtures test
nine concurrent attempts, independent execution counts, non-resetting lifetime limits
and timeout/over-budget descendant cleanup. They produce no model recording. The capture
loads its generated configuration using both boolean `--strict-mcp-config` and explicit
`--mcp-config <path>`. Interactive example: `claude --strict-mcp-config --mcp-config examples/claude-code-stdio.json`.

The recording command uses a read-only stdio host, a strict two-tool allowlist, at most six
assistant turns/eight dispatched calls and a 120-second deadline. A local capture-only
stdio adapter atomically reserves each allowed read before forwarding; a private
SQLite ledger spans adapter restarts and never resets on a minute boundary. The ninth
attempt is denied before reaching the SDK host and fails the capture. This is separate
from the server's normal per-minute quota. Capture requires Unix process groups: Claude
starts in an owned group, the adapter verifies membership before starting its server,
and timeout/budget/failure cleanup terminates that owned group including descendants. It rejects API-key billing,
removes alternate-provider environment settings and preserves the actual final answer
unchanged. Schema/factual validation must pass before it saves the capture. The prompt
is versioned in `evals/prompts/read-support-v1.txt`. The recording contains prompt/answer
hashes, product commit, fixture hash, actual calls and safe audit correlations. No raw
session initialization, account metadata, secrets or system transcript is saved.

Observed usage is retained only when exposed by the client. Cost stays null with its
reason; offline replay is not a measured token/cost benchmark. Review the sanitized
capture before adding it to the repository. The paginated renderer labels its output
as a replay and shows the entire original answer early; it never synthesizes speech.

The separate deterministic client demonstrates write denial, preparation, **human
operator adapter** approval, submission and same-key restart retry:

```bash
# On Studio, this is a heavy real-process check:
~/.local/bin/studio-heavy-run -- bash -c 'source scripts/environment.sh; python3 scripts/process-acceptance.py'
```

The PTY is automated test composition, not proof that a real person watched that run.
For a human demo use README's separate-terminal workflow. Claude never receives shell
access or approves its own intent. No payment is issued.

Authored `evals/fixtures/scripted-answer.json` and scenario decisions explicitly say
scripted. They are useful offline contract fixtures and are never relabelled Claude.
