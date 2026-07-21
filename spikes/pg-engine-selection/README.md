# Spike: Postgres engine selection (Phase 4 G1.3)

Throwaway code. The deliverable is the evaluation it produces, not this code —
per [ADR-0003](../../docs/adr/0003-postgres-as-second-provider.md), the engine choice
was deferred until the work begins, and this spike is how the deferral is resolved:
by measurement, not by reading feature lists.

## Scenario (identical for every candidate)

A four-table schema (uuid PKs, FK CASCADE, UNIQUE, `CHECK (... IN (...))`,
`timestamptz`, dedicated non-`public` schema) exercised as:

1. Empty database → apply `fixtures/v1` → **re-diff must be empty**
2. Apply the v2 delta (added column + CHECK + UNIQUE, `fixtures/v2`) → **re-diff must be empty**

The `CHECK (... IN (...))` expression is deliberate: Postgres rewrites it to
`= ANY (ARRAY[...])` on storage, so any engine that compares expression *text*
re-diffs forever. This is the same defect class SQL Server exhibits today
([limitations](../../docs/limitations.md)) — the axis with the most evidence
behind it, and the one a candidate must survive.

## Candidates

- `psqldef/` — wrapping [psqldef](https://github.com/sqldef/sqldef) as a subprocess.
  Run log in `run-scenario.md`; the binary itself is not committed.
- `native-catalog/` — direct `pg_catalog` introspection over Npgsql, with
  *shadow-database normalization*: the desired state is applied to a scratch
  database and both sides are compared in the engine's own canonical form
  (the technique behind migra and stripe/pg-schema-diff).

## Environment

Dedicated container — never a shared one:

```bash
docker run -d --name schemorph-pg-spike -e POSTGRES_PASSWORD=spike -p 15544:5432 postgres:16-alpine
docker exec schemorph-pg-spike psql -U postgres -c "CREATE ROLE spike_app LOGIN PASSWORD 'spike_app' NOSUPERUSER NOCREATEDB NOCREATEROLE;"
docker exec schemorph-pg-spike psql -U postgres -c "CREATE DATABASE spike_target OWNER spike_app;"
docker exec schemorph-pg-spike psql -U postgres -c "CREATE DATABASE spike_shadow OWNER spike_app;"
```

All candidate runs use the `spike_app` login. Anything that only works as a
superuser does not count as passing (managed-Postgres requirement R1).
