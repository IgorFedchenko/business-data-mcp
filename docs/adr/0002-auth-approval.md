# 2. Scoped API keys and a separate operator

Status: accepted.

HTTP uses distinct local read/write API keys and a global write switch, not an OAuth
server. stdio defaults to read-only local process trust. Discovery and direct calls
both enforce permissions; annotations are explanatory only.

Preparation needs write permission but no approval. Submission also needs the original
actor's unexpired intent and human approval. A separate terminal adapter shows the
exact action and requires the full ID. No MCP approval tool exists.

The trusted operator may approve another principal's displayed intent. This boundary
protects MCP-only callers. A hostile shell/DB owner can alter state or automate a PTY;
it is not an isolated human-identity service. Production approval needs a separate UI,
authentication and storage privileges. No payment happens here.
