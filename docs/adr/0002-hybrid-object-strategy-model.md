# ADR-0002: Hybrid object-strategy model (diff + idempotent + versioned)

- **Status:** Accepted
- **Date:** 2026-07

## Context

Schema management tools historically pick one mechanism and force every object through it:

- **Pure declarative diff** (SSDT, Atlas, sqldef): excellent for tables and indexes, but procedure/function bodies are opaque text — structural diffing of them is unreliable in every tool that attempts it (missed changes, spurious changes, dependency-order failures). Data changes (seeds, backfills) cannot be expressed as state at all.
- **Pure versioned migrations** (Flyway, Liquibase, EF Migrations): correct for data changes and auditable, but the *current* schema is buried under the accumulated history of deltas; understanding "what does the users table look like today" requires replaying or trusting a snapshot. Hand-writing ALTERs is error-prone busywork that diffing solves.

Meanwhile, both SQL Server (2016+) and PostgreSQL natively provide `CREATE OR ALTER` / `CREATE OR REPLACE` for programmable objects — an idempotent mechanism that makes body-diffing unnecessary: re-applying the full definition is always correct.

## Decision

Route each object class to the mechanism that fits it:

1. **Structural objects** (tables, columns, indexes, constraints) → declarative diff against desired-state SQL files.
2. **Programmable objects** (procedures, functions, views, triggers) → one object per file, applied via idempotent re-definition when the file's checksum changes; ordered by dependencies.
3. **Data changes** → versioned, checksummed, run-once migration files tracked in the history ledger.

All three record into the same ledger, so a single audit trail covers the database's full change history.

## Consequences

**Positive**

- Each mechanism is used only where it is reliable; the known failure modes of single-mechanism tools are avoided by construction rather than mitigated.
- Programmable-object changes review naturally: the meaningful diff is the git diff of the body, and the plan simply reports *changed → will re-apply*.
- The current schema is always directly readable from `schema/` — no replay needed.

**Negative / accepted risks**

- Users must learn which directory an object belongs in. Mitigated by making `inspect` generate the correct layout, and by clear errors when a file is misplaced.
- Edge-case object types (sequences, user-defined types, synonyms, permissions, ...) need per-type classification decisions. These are deferred to implementation, decided per-provider, and recorded as they are made.
- Idempotent re-definition of a procedure is not transactionally coupled with a structural change it depends on in all cases; apply-ordering rules across strategy boundaries need careful design during implementation.

## Alternatives considered

- **Diff everything, including procedure bodies** (SSDT's approach): rejected as the primary mechanism because body-diff fragility is a core pain this project exists to remove; DacFx's body handling remains available as a fallback where `CREATE OR ALTER` is unavailable.
- **Version everything** (Flyway-only): rejected; loses the declarative readability of the current schema and reintroduces hand-written ALTERs.
