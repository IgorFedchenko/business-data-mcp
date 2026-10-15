# 1. SQLite and integer-cent synthetic data

Status: accepted.

Use file SQLite with parameterized SQL and explicit transactions. Four synthetic
orders make the reference easy to run without an account, Docker or network database.
Money is integer cents plus currency. The independent expected-facts fixture is
manually authored from the requirements, separate from schema/seed implementation.

One writer transaction and unique actor/key, intent and order constraints protect
return creation. This choice does not provide distributed quotas or a multi-tenant
service. A PostgreSQL adapter is a future integration, not hidden behind a generic ORM.
