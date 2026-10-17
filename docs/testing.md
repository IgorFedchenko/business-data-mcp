# Testing

Run `./scripts/verify.sh` with the [required tooling](tooling.md). On Studio it owns
one heavy ticket; never nest wrappers. The suite fails on missing tools or checks.
The local run passed 57 tests with zero failures or skips, real stdio/Kestrel/PTY and
restart checks, four hard-crash recovery cases, 16 scripted MCP assertions and the
renamed-template, Docker and clean-copy cold-start checks. Independent verification
removed direct-call write authorization and corrupted an authored answer fact in
disposable copies: both mutations reached and failed the relevant assertions after
successful builds. Unmutated and restored permission tests and all 16 scripted
assertions passed. These local checks do not imply publication approval.

The layers cover core invariants, official SDK in-memory clients, ASP.NET TestServer,
independent raw JSON-RPC against real stdio/Kestrel processes, Unix PTY operator
approval, independent SQLite assertions, races, replay and restart. A separate test-only
SDK host pauses before execution or after a business commit, then an external client
SIGKILLs it and verifies recovery through the production host. No production failure
toggle is used. Template/Docker checks use isolated state and owned-process cleanup.

`python3 scripts/cold-start.py` is an outer clean-copy check: its copied run/build
scripts own compilation's heavy ticket. Run it separately, without an outer wrapper.

`python3 scripts/evaluate.py` executes scripted MCP contract scenarios against the
real server and a manually frozen synthetic oracle. Amounts, names, statuses and
customer membership are literal independent expectations, never learned from output.
Evaluator negative controls corrupt facts/schema and must fail. This measures MCP
contract behavior, not model intelligence or live prompt-injection immunity.
Optional `--require-capture` also validates genuine captured response provenance;
missing or forged evidence fails. No inference runs in local verification.

Focused mutation verification belongs in disposable copies and separate heavy tickets.
A valid mutation must compile and fail a relevant assertion, not merely fail setup.
Private hosted Actions is forbidden; public-only workflow configuration is dormant.
