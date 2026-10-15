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
