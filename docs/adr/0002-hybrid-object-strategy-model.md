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

## Addendum — brownfield reconciliation (2026-07-12)

Dogfooding a real SSDT-deployed database surfaced a gap in strategy 2's judgment:
"no ledger history" was treated as "pending re-definition", so adopting a database
Schemorph did not build reported every programmable object as a phantom redefine
(22 of 22 views on the first real target), and the first `apply` executed that many
unnecessary `CREATE OR ALTER`s just to seed the ledger.

**Refinement**: a history-less object whose live definition already matches its file
is *reconciled*, not redefined — `apply` records the file checksum in the ledger
(operation `Reconcile`) without executing anything, and `diff` reports no change.
Matching is a provider capability (`FilterMatchingLiveDefinitionsAsync`; on SQL Server,
`sys.sql_modules` preserves deployed batch text verbatim) and is deliberately strict:
only line endings, outer whitespace, a trailing batch semicolon, and
`CREATE OR ALTER` ↔ `CREATE` are equivalent. A false match would silently adopt a
differing object; a false mismatch merely costs one idempotent redefinition — so
all doubt resolves to "no match". Objects *with* history never consult the live
definition: a recorded-but-different checksum means the files moved on, and edits
always win. The anchor is unchanged — this makes strategy 2's answer to "what needs
applying?" state-honest, the same standard strategy 1 already met.

## Addendum — layout is a convention, not a contract (2026-07-20)

One accepted risk above is obsolete and would now mislead a reader. It said:

> Users must learn which directory an object belongs in. Mitigated by making
> `inspect` generate the correct layout, and by clear errors when a file is misplaced.

Implementation went further than the mitigation. The desired-state loader classifies
by **content, not by directory** — it parses each file and routes on what the
statement *is*. A `CREATE PROCEDURE` saved under `tables/` is routed to strategy 2 and
re-defined normally; no error is raised, because nothing is wrong. Verified against a
live database: such a file plans as `redefine Procedure`, alongside the table in the
same directory planning as a structural change.

So there is no "misplaced file" error, and users do not have to learn the layout. The
conventional layout is what `inspect` *writes* and what makes a tree readable to
humans — not what the tool requires to work. That is also what lets brownfield SSDT
trees be consumed as-is, which the roadmap treats as a first-class adoption path.

The remaining true half of that risk is narrower: users must still know **which
strategy** an object gets (declarative diff vs. idempotent re-definition vs. versioned
migration), because that governs how a change is reviewed and applied. That knowledge
is about object kinds, not folders.
