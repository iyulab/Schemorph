---
name: schemorph
description: Manage a SQL Server schema declaratively with Schemorph — edit desired-state .sql files, compute a reviewable plan (diff), and execute it through the fingerprint-gated apply. Use when asked to change database schema (tables, views, procedures, functions, triggers), add versioned data migrations, check schema drift or migration status, or adopt an existing database into declarative management. Covers both the schemorph CLI and its MCP server.
---

# Schemorph: declarative SQL Server schema changes

Schemorph treats a directory of `.sql` files as the desired state. You never
write ALTER statements for structure — you edit the CREATE, and Schemorph plans
and executes the transition. Data changes are events, not state: they go in
versioned migration files, never in the desired state.

## Prerequisites

- `schemorph` on PATH (`dotnet tool install -g Schemorph`) or the MCP server
  configured (`schemorph mcp` over stdio).
- Connection string in the `SCHEMORPH_URL` environment variable. Never pass
  credentials as arguments or through an MCP conversation; password material is
  redacted from all output, but the environment variable is the contract.

## Repository layout

```
schema/            # desired state: one object per file, grouped by kind
  tables/dbo.Orders.sql        # full CREATE TABLE incl. constraints/indexes
  views/dbo.OrderTotals.sql
  procedures/ functions/ triggers/
migrations/        # versioned, run-once data events
  V0001__backfill_totals.sql   # V####__description.sql, immutable once applied
```

## The change loop (always this order)

1. **Edit** the desired-state file(s). To change a table, change its CREATE.
   To drop an object, delete its file. Programmable objects (views, procedures,
   functions, triggers) are re-defined idempotently from their file — same edit
   model.
2. **Plan**: `schemorph diff --schema ./schema` (JSON on redirected stdout).
   Exit `0` = nothing to do, `2` = changes pending, `1` = error.
3. **Review the plan JSON** (contract: `docs/plan-format.md`):
   - `changes[]` — each with `actions`, `risk` (`safe`/`warning`/`destructive`),
     `sql` (what will run, when attributable), `explanation` (why).
   - `messages[]` — includes safety-lint warnings in the `SCHEMORPH1xx` band
     (NOT NULL without default, table rebuild cost, destructive included).
     Take them seriously; they never block by themselves.
   - `planHash` — the fingerprint of exactly what would execute.
4. **Apply the reviewed plan and nothing else**:
   `schemorph apply --schema ./schema --expect-plan <planHash>`.
   If anything drifted since review, apply refuses with error code
   `plan_mismatch` and nothing has executed — re-run diff, re-review, retry
   with the new hash. Do not retry a mismatch with the old hash.
5. **Converge**: `schemorph status --schema ./schema` — exit `0` and
   `hasPendingWork: false` confirm the database matches the files.

If step 5 does not converge — the *same* change reappears after a successful
apply — stop and read `changes[].sql`. An expression the engine stores in its own
form (most often `CHECK (… IN (…))`, which SQL Server keeps as an OR chain) will
re-diff forever: the database is correct and the plan is not. Do not re-apply in a
loop. Rewrite the expression in the stored form — `schemorph inspect` renders it —
or report the file to the human. See `docs/limitations.md`.

Destructive changes (DROPs that lose data) are excluded from plans by default
and surfaced as messages; include them only with an explicit
`--allow-destructive` on both diff and apply, after confirming intent.

## Versioned migrations (data events)

- Add a new `V####__description.sql`; pass `--migrations ./migrations` to
  `apply` (runs pending ones, records each in the ledger) and to `status`
  (reports pending + lint warnings: TRUNCATE, unfiltered UPDATE/DELETE,
  GRANT/REVOKE/DENY).
- **Never edit an applied migration** — Schemorph fail-fasts on tamper
  (`migration_failed`). Fix forward with a new migration.
- There are no down migrations: to roll back, apply the previous git state.

## Machine contracts (parse these, not console text)

- JSON is the default whenever stdout is redirected; `--format json` forces it.
- `diff --format sql` is the *human* artifact, not a machine one: the whole plan as
  one read-only document with the `planHash` in its header. Hand it to a person for
  approval, then apply with that hash — never execute it with a SQL client.
- `schemorph schema` prints a JSON manifest of every verb, flag and exit code.
- Errors: one JSON envelope on stderr — `{error: {kind, code, message, hint}}`
  (`docs/errors.md`). Branch on `kind`: `usage` → fix invocation;
  `invalid_state` → fix files, retrying unchanged fails identically;
  `execution` → inspect and retry (apply is convergent and safe to re-run).
- A failed `apply` adds `stage` and `committed{declarative, redefines, migrations}` —
  read those instead of guessing what the database holds. Optional fields are
  absent rather than null, and `hint` is omitted when no cause was established.

## MCP mode

Tools mirror the CLI: `schemorph_diff`, `schemorph_status`,
`schemorph_inspect` (read-only) and `schemorph_apply` (requires
`expectedPlanHash` — same gate as `--expect-plan`). Resources give context
without tool calls: `schemorph://schema` (live schema as desired-state SQL),
`schemorph://schema/{kind}/{name}` (one object), `schemorph://plan` (current
plan incl. `planHash`; needs `SCHEMORPH_SCHEMA_DIR` in the server env).

## Adopting an existing database (brownfield)

`schemorph inspect --out ./schema` writes the live database as desired-state
files. History-less programmable objects whose live definition already matches
their file are *reconciled* (recorded, nothing executed) — a clean first apply
on a database Schemorph did not build is expected, not suspicious.
