# Implementation and acceptance

Reference implementation for an assistant that looks up orders and customer history,
and opens a return ticket only after scoped authorization and a human approval.

1. Core: deterministic SQLite seed, bounded parameterized queries, return state machine,
   expiry, subject binding and durable idempotency. Verify stored rows, including denied writes.
2. MCP: official SDK tools, stdio and Streamable HTTP, role-aware discovery and execution,
   API-key authentication, per-principal limits and redacted audit for successful/denied/error calls.
3. Tests: business invariants, actual MCP client/server over in-memory pipes, HTTP authentication,
   authorization, origin checks and quota. Run on .NET 8 locally and GitHub Actions.
4. Delivery: one-command launch, Claude Desktop/Code examples, recorded real Claude conversation,
   architecture/security documentation, MIT license and optional dotnet new template.
5. Review: private GitHub repository, green checks and portfolio copy. Publication only after
   Igor's explicit review and approval.

Terminal artifacts: passing `scripts/verify.sh`, green GitHub workflow on the delivered SHA,
`docs/demo.gif` plus provenance for the real tool calls, and a tested template install/create/run.

Scope: SQLite only; no external payment/refund service, OAuth provider, Google Sheets or paid
hosting. API keys are the selected HTTP authentication option. Public demo is a repository GIF.
