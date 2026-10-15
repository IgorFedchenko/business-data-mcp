# Demo provenance

The intended visual artifact is a **rendered replay of real Claude Code tool calls**,
with delays condensed. It is not a simulated Claude answer or a live screen recording.
Only synthetic data is eligible for this capture.

## Capture

1. Build the server with `./scripts/build.sh`.
2. Use an existing first-party Claude OAuth login. The recording script refuses API-key
   authentication. Any new paid usage or model key requires separate owner approval.
3. Run `python3 scripts/record-claude-demo.py`.
4. Review `docs/demo-transcript.json`. It contains only the user prompt, allowlisted
   read-tool calls/results, final answer, model name and sanitized server audit.
   Raw CLI initialization, account metadata, system prompts and credentials are not stored.
5. Install Pillow in an isolated Python environment if needed, then run
   `python3 scripts/render-demo.py` to create `docs/demo.gif` and `docs/demo.png`.

The recorder uses a temporary database and a strict MCP configuration that connects
only this server. Built-in tools, file access, shell execution, agents and write tools
are unavailable. Both `get_order` and `get_customer_history` must have successful
server audit rows; otherwise the recording is rejected. No LLM call occurs in CI.

## Deterministic smoke

`python3 scripts/protocol-demo.py` speaks real MCP JSON-RPC to a fresh stdio server.
It checks initialization, discovery, an actual order query and a direct unauthorized
write rejection. This checks protocol behavior independently of any model response.
