# 3. Durable retry keys and explicit audit uncertainty

Status: accepted.

Business mutation, retry-key binding and intent consumption commit atomically. Audit
start and completion use separate transactions to keep this reference small. A durable
start precedes tool execution. A generated correlation ID exists even if start fails.

| Failure | Response if deliverable | State |
|---|---|---|
| Start persistence | `audit_unavailable` | No tool execution |
| Completion after read/denied/invalid | `audit_unavailable`, original code | Started may remain; no write |
| Completion after successful write | `outcome_unknown`, original code | May have committed; reuse original key |
| Crash/cancel/disconnect | Delivery is not guaranteed | Inspect rows and retry same key |

Completion failure never escapes a `finally` and overwrites the safe response. The
injectable audit boundary is a test composition seam, never a production failure flag.
Audit retains safe identity/count metadata, not payloads, retry keys, names or query text.
Created/replayed disposition is omitted when not known to the filter; identity is stable.
The log is mutable by its local owner and has no retention/backup/tamper-proof guarantee.
