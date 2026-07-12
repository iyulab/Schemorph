# ADR-0004: Failure semantics, resume, and ledger crash-consistency

- **Status:** Proposed (decision points below need a human call)
- **Date:** 2026-07

## Context

`apply` is a four-stage pipeline: declarative publish → drop tombstones → idempotent
re-definitions → versioned migrations. Each stage can fail mid-stream (connection loss,
Nth statement failure, process kill), and SQL Server auto-commits each DDL statement
that is not inside an explicit transaction. The ledger (`__SchemorphHistory`) promises a
*single audit trail*; that promise is only as good as its behavior around failures.

What the code does today, stage by stage:

| Stage | Execution | Ledger write | Crash window | Severity |
|---|---|---|---|---|
| Declarative | DacFx `PublishChangesToDatabase`, **non-transactional** (`IncludeTransactionalScripts` defaults to false) | After success, separate connection, one row per change | Publish fails mid-script → partial DDL applied, **no ledger rows at all**; or publish succeeds and the process dies before recording | Medium — the diff re-derives state from the live database, so convergence survives; the audit trail does not |
| Drop tombstones | n/a (bookkeeping only) | Separate connection | Dies before writing → a re-added identical file is wrongly skipped next run | Medium |
| Redefine | One script per object, each in its own transaction | Per object, after its script commits, separate connection | Object applied but unrecorded → next run re-applies it | Benign — `CREATE OR ALTER` is idempotent |
| Migration | Whole script in **one transaction** | After the script's transaction commits, **separate connection** | Script committed, process dies before the ledger row → **next run executes the migration again** | **Critical — violates the run-once contract** |

Failures are additionally invisible in the ledger: no code path ever writes a
`Succeeded = false` row, so a failed apply leaves either nothing or only the rows of
earlier stages.

Reference behavior elsewhere: Flyway runs each migration in a transaction on databases
with transactional DDL and rolls back on failure, marks statements it detects as
non-transactional and runs them outside, and leaves a `success = 0` history row that
`flyway repair` cleans up. SQL Server supports transactional DDL for the object classes
Schemorph manages (tables, indexes, constraints, programmables); the known exceptions
(`CREATE/ALTER DATABASE`, full-text, some server-level statements) are outside
Schemorph's Phase-1 surface.

## Decision

### 1. Resume model: convergent re-run (no checkpoints)

A failed `apply` is resumed by **running `apply` again**, nothing else. Each strategy is
(or is made) convergent:

- Declarative: the diff is recomputed against the live database, so whatever partially
  applied is simply part of the next delta. Convergent by construction.
- Redefine: checksum-vs-ledger comparison; a re-applied unchanged object is a no-op.
- Migration: run-once check against the ledger — convergent **only** with decision 2.

Checkpoint/resume-token designs are rejected: a checkpoint is one more piece of state
that can itself desynchronize from the database, and it buys nothing once every stage is
convergent. There is no `schemorph repair` because there is nothing to repair by hand.

### 2. Migration atomicity: script and ledger row commit in the same transaction

The migration script and its ledger `INSERT` must be one atomic unit. This closes the
critical window: either the migration ran *and* is recorded, or neither happened.

Mechanism: the provider executes the script and appends the ledger entries inside the
same `SqlTransaction`. The core expresses the intent through the provider contract
(e.g. an `ExecuteScriptAsync` overload that takes the ledger entries to commit with the
script); the dialect INSERT stays in the provider, semantics stay in the core — the
existing policy/mechanism split.

Statements that cannot run inside a transaction are out of Phase-1 scope; if a user
script contains one, SQL Server raises an error, the transaction rolls back, and the
error surfaces verbatim. A per-file opt-out directive (cf. Flyway's non-transactional
marking) is deferred until real demand.

The same coupling applies to redefines (script + redefine row in one transaction) for
audit-trail completeness, even though their crash window is behaviorally benign.

### 3. Declarative publish becomes transactional

Set `IncludeTransactionalScripts = true` on the comparison's `DacDeployOptions`
(`SchemaComparison.Options` *is* a `DacDeployOptions`): DacFx wraps the update script so
changes commit only after all of them apply. A mid-stream failure leaves the schema as
it was, instead of half-migrated. Ledger rows for the publish are written after the
publish commits (decision 5 covers the residual window).

Accepted residual risk: DacFx itself places operations it knows to be non-transactional
outside the transaction; for the Phase-1 object surface none are expected, and the
golden-corpus suite (ROADMAP § Phase 1) is the guard if a DacFx upgrade changes that.

### 4. Failures are recorded: `Succeeded = false` rows

When a stage fails, Schemorph appends a failure row (best effort) with the failing
object/script name and the error text in `Detail`. Consumers of the ledger — including
future `status` — can then distinguish "never attempted" from "attempted and failed".
Failure rows never affect resume logic (readers already filter on `Succeeded = 1`), and
a failure while writing the failure row must never mask the original error.

### 5. Pipeline is fail-fast; no cross-stage rollback

The pipeline stops at the first failed stage; earlier stages stay applied and recorded;
exit code 1. Rolling back an earlier *committed* stage would require compensating
actions, which is rollback-ADR territory ([ADR-0005](0005-rollback-semantics.md)) and
largely unsupported there. The residual "published but process died before ledger rows"
window (declarative stage) is accepted: it costs audit completeness, not convergence,
and closing it would require folding DacFx's publish into a transaction we own —
disproportionate for Phase 1 (revisit if the ledger gains consumers that require
completeness, e.g. drift attribution).

### 6. Cross-strategy ordering limitation: documented, not auto-resolved

The known failure (a **new** table whose computed column/constraint references a **new**
user function fails, because declarative publish runs before redefines) is handled by
documentation and a clear error hint suggesting the two-step apply (add the function
first, then the table). See "Decision points" — the alternatives are recorded there
because this is a product-behavior trade-off.

## Decision points for a human

1. **Resume = convergent re-run** (recommended, decision 1) vs. checkpoint/repair
   tooling. Re-run keeps zero extra state; checkpointing only pays off if stages stop
   being convergent, which nothing on the roadmap implies.
2. **Cross-strategy ordering** (decision 6): (a) document + error hint *(recommended —
   smallest correct behavior, honest about the boundary)*, (b) pre-create new
   programmables before the declarative publish (fails the mirrored case: new function
   referencing a new table), (c) bounded convergence loop — declarative → redefine →
   retry declarative once (resolves one-level cycles but makes stage failure part of the
   normal path and can mask real errors). Recommendation: (a) now; promote to (c) only
   on real dogfooding demand.

## Consequences

**Positive**

- "Re-run until green" becomes a documented, safe operator instruction — also the ideal
  contract for agent callers (no special resume verbs to learn).
- The run-once contract for migrations survives crashes; the audit trail gains failures.
- No new user-facing surface: no repair verb, no checkpoint files.

**Negative / accepted risks**

- Earlier stages of a failed apply remain committed (no cross-stage rollback);
  the plan output is the tool for previewing blast radius before applying.
- The declarative "published but unrecorded" window persists (audit-only impact).
- `IncludeTransactionalScripts` makes large publishes hold longer transactions
  (lock/log growth on big deltas) — acceptable at MVP scale, revisit with telemetry.

## Alternatives considered

- **Checkpoint files / resume tokens** — rejected (decision 1): extra state, no benefit
  once stages are convergent.
- **A `repair` verb (Flyway-style)** — rejected: repair exists to fix a history table
  that disagrees with reality; same-transaction coupling prevents the disagreement
  instead of curing it.
- **Wrapping the entire pipeline in one transaction** — rejected: DacFx owns its own
  connection/execution for the publish, and a single giant transaction across strategies
  would serialize everything behind the largest lock footprint while still not covering
  non-transactional statements.
