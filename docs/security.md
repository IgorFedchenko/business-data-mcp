# Security and reliability decisions

This is a **single-tenant reference implementation**, using only four synthetic orders.
It is an example to adapt and review, not a hosted multi-tenant product.

## Trust boundaries

- HTTP: an `X-Api-Key` header authenticates `api:reader` or `api:writer`. Keys must have
  32–512 non-whitespace characters and be different. Generated random keys are recommended.
  Comparison uses fixed-time SHA-256 digest comparison. Keys in query strings or JSON
  tool arguments are never accepted. This is API-key authentication, **not MCP OAuth**.
- Write access requires BOTH `MCP_ALLOW_WRITES=true` and a separate write key for HTTP.
  Without the switch, even a configured write key is rejected. stdio inherits the local
  operator's trust; it defaults to read-only and needs the same explicit write switch.
- `tools/list` hides writes from readers. The `tools/call` filter independently enforces
  the same permission check, including direct invocation of a hidden tool.
- `prepare_return` creates an immutable intent bound to the actor, order and enumerated
  reason. It expires ten minutes after preparation. Only the interactive local
  `approve-return` CLI can approve it. There is no approval MCP tool, no `confirmed=true`
  argument and no confirmation secret returned for a model to echo back.
- The operator sees the exact order, amount, reason, actor and expiry and types the full
  intent ID. Redirected stdin and a mismatching answer are rejected. `request_return`
  rechecks identity, approval, expiry, order eligibility and previous consumption.
- Anyone controlling the process environment, local SQLite file or operator terminal is
  trusted. Do not give an untrusted agent shell access to these resources. In a real
  deployment put approval in a separately authenticated UI/service and database role.
  Human approval here is an application control, not a guarantee against a hostile host.

## HTTP boundaries

HTTP binds to `127.0.0.1:5050` by default; stdio opens no listening socket. The default
host allowlist is localhost/loopback. Unrecognized browser origins are rejected; no
cross-origin browser policy is enabled. Origin checks supplement authentication and
host validation; they do not replace either. Non-loopback plaintext HTTP requests are
rejected. For remote use, configure HTTPS and an explicit `AllowedHosts` value, or use a
trusted TLS-terminating proxy over a private loopback connection. Forwarded headers
are not trusted automatically. Never expose this demo directly to the internet.

The HTTP body limit is 64 KiB. Requests have per-key fixed-window quotas (120 HTTP
requests/minute) and all transports have a separate per-principal tool quota (30
calls per UTC minute). Denied and invalid tool calls count. HTTP rejections return
429 and `Retry-After: 60`; tool rejections return `isError` with `rate_limited` and a
retry instruction. No automatic unbounded retry exists. Limits are process-local;
multiple replicas need a shared rate limiter and shared approval/idempotency store.

## Data integrity and errors

All SQL parameters are bound. Searches escape wildcard characters, cap input length
and return at most 20 rows. Customer history is bounded to the latest 20 orders and
20 returns. Money uses integer cents plus currency, with no floating-point arithmetic.

The return flow only opens a synthetic full-order return ticket. It never sends a
refund, contacts a merchant or changes fulfillment status. Only delivered orders
are eligible; this is a deliberately small demo policy, not a commercial returns policy.

SQLite immediate transactions serialize return writes. Database constraints enforce
one ticket per order, one consumption per intent, and a unique idempotency key per
principal and operation. Preparation keys and submission keys have separate namespaces.
Identical retries replay the original result, including across restarts and after
approval expiry. Reusing a key for different arguments returns `idempotency_conflict`.
A new key cannot reuse a consumed intent. A failed insertion rolls back consumption.

Clients should retry transient connection failures or `internal_error` with bounded
backoff and the **same** idempotency key. Do not retry validation, authorization,
conflict or expired-confirmation errors unchanged. `expired_confirmation` requires
preparing and approving a new intent. A cancelled write may already have committed;
use its original key to resolve uncertainty. SQLite has a five-second busy timeout.
No external service calls or payment retries are present.

## Audit and privacy

Every routed `tools/call`, including unknown tools, validation errors, denials and
quota rejections, receives a durable `started` SQLite audit record before execution.
Completion records actor, outcome, stable result code and duration; it does not copy
result bodies. IDs and enumerated reasons are allowlisted. Search queries, idempotency
keys, invalid IDs and arbitrary extra arguments are redacted or omitted. Unknown tool
names are replaced with `unknown_tool`. HTTP security rejections are separate
`http.access` events without headers, URLs or bodies. Operator approvals are recorded.

Protocol discovery, initialization, pings and successful HTTP envelope requests are
not business-tool calls and are not separately audited. A request rejected before it
can be parsed as `tools/call` is outside the tool audit filter. SDK wire logging is
disabled; application console logging goes to stderr to preserve stdio framing.

An audit-write failure prevents tool execution. A completion-write failure does not
silently report success. Business state and audit completion use separate transactions:
a crash may leave `started` even if a write committed. Resolve it from durable
idempotency records; do not interpret `started` as a rolled-back operation. This demo
log is mutable by the database owner, not tamper-evident. No retention policy or external
log shipping is implemented. Store the database with restricted OS permissions and
add retention, backup/restore and tamper-resistant logging for a production system.

No API keys, LLM API keys, employer/client datasets, real customers or external service
credentials are included. Tests and the deterministic protocol demo call no LLM.
