# Architecture

The server is a synthetic business-data reference with two production assemblies.
Core owns bounded parameterized SQLite reads, immutable actor-bound return intents,
idempotency and audit storage. Server adapts those rules to the official MCP SDK,
stdio and Streamable HTTP. Both transports share tool registration and policy.

The README Mermaid diagram shows the request path. Authentication precedes HTTP
principal quotas; tool authorization applies independently of discovery annotations.
Local stdio is read-only by default. HTTP uses separate read/write keys and an explicit
write switch. An MCP caller cannot approve its own intent: an interactive operator
CLI updates the same SQLite database outside the MCP surface.

Return creation and idempotency commit atomically. Audit start and completion use
separate transactions; a crash after business commit can leave a started audit row.
Retrying the original key recovers the same durable identity. This is not a distributed
transaction or tamper-evident log. See [security](security.md) and [audit decisions](adr/0003-audit-idempotency.md).

Key decisions: [SQLite](adr/0001-storage.md), [authentication and approval](adr/0002-auth-approval.md),
[audit/idempotency](adr/0003-audit-idempotency.md), [evaluation scope](adr/0004-evaluation.md).
Test-only crash composition references production assemblies; production has no
reference to the fixture and ships no failure switch. No model or remote service is
part of the business flow. Telemetry has no outbound exporter by default.
