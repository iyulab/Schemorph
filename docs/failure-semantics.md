# What happens when an apply fails

The question this answers: *the plan had 31 changes and something went wrong at the
17th — what does the database look like now, and what do I do?*

Short answer: **re-run it.** The longer answer is below, because "re-run it" is only
safe if you know why, and because the state you are left in depends on which stage
failed.

## An apply runs in three stages

They always run in this order, and each has its own failure behaviour.

| # | Stage | What it does | If it fails |
|---|---|---|---|
| 1 | **Declarative publish** | Every structural change — tables, columns, indexes, constraints — as one publish | **All of it rolls back.** No change from this stage survives |
| 2 | **Redefinitions** | Each programmable object (view, procedure, function, trigger) re-defined idempotently | Stops at the first failure. Objects before it stay re-defined; that one and the rest do not |
| 3 | **Migrations** | Versioned run-once data scripts, in order | Stops at the first failure. That script rolls back whole; earlier ones stay applied |

Stage 1 is atomic because the publish is wrapped in a transaction
([ADR-0004 §3](adr/0004-failure-semantics-and-resume.md)). So for a 31-change plan
where 8 are column alters and 23 are view redefinitions, "it failed at the 17th"
resolves to:

- **Failed during the 8 alters** → *none* of them applied. The schema is untouched.
- **Failed during the 23 redefinitions** → all 8 alters are committed, and the views
  up to the failing one are re-defined. The database is part-way, on purpose.

**There is no rollback across stages.** Stage 2 failing does not undo stage 1. That
is deliberate: undoing a committed structural change is itself a destructive
operation, and the tool does not perform destructive operations you did not ask for.

**Which of these behaviours applies to the run in front of you is declared in the
plan itself**: the `atomicity` field ([plan format 1.3](plan-format.md)). Everything
on this page describes `atomicity: "partial"` — stages commit independently — and
that is what SQL Server declares. A provider that owns the whole transaction boundary
declares `transactional` instead: the apply lands whole or not at all, and "part-way,
on purpose" cannot happen. Read the field, not the engine name
([ADR-0004 addendum](adr/0004-failure-semantics-and-resume.md)).

Stage 3's atomicity is per script, and the run-once record commits *in the same
transaction as the script itself*, so a crash can never leave a migration applied but
unrecorded — the case that would silently run it twice.

## Recovery: re-run the same command

There is no `schemorph repair`, and that is not an omission. Every stage is written
so that running it again converges:

- The declarative publish re-compares against the live database. Changes that already
  applied are simply not in the new plan.
- Redefinitions are `CREATE OR ALTER` against a checksum. Objects already matching
  their file are skipped; the one that failed is retried.
- Migrations are run-once by checksum. Applied ones are not re-run; the failed one is.

So the safe recovery is: **fix what caused the failure, then run the same `apply`
again.** If you use `--expect-plan`, re-run `diff` first — the plan legitimately
changed, because part of it is now applied.

What is *not* safe is hand-editing the database to "finish" the apply, or deleting
ledger rows to force a re-run. Both put the ledger and the database out of step, and
the ledger is what makes the run-once guarantee work.

## The failure itself tells you where it stopped

Reading the ledger is the audit path, not the first step. A failed `apply` reports
the stage it stopped in and how much of each stage had committed, so the common
question is answered without querying anything:

```json
{ "error": { "code": "migration_execution_failed", "stage": "migration",
             "committed": { "declarative": 8, "redefines": 23, "migrations": 0 } } }
```

In text mode the same counts appear in the hint. `stage` is absent on failures that
never reached the database (see the last section). The full contract is in
[errors.md](errors.md#a-failed-apply-stage-and-committed).

## Reading the ledger after a failure

The ledger is Schemorph's audit trail, and this is the part most likely to be
misread: **a row means something different depending on its kind.**

| Kind | When rows are written | So a row means |
|---|---|---|
| `declarative` | **In one batch, after the publish has committed** | The publish succeeded and this is the record of it |
| `redefine` | One at a time, in the same transaction as each object's script | This object is done; the ones without rows are not |
| `migration` | One at a time, in the same transaction as each script | This script is done; the ones without rows are not |

Only the last two are a progress log. `declarative` rows are written *after the fact*,
all together — so a plan with 8 declarative changes shows either 8 rows or 0, never 3.

This matters because the rows look alike: same table, same success flag, same
per-object shape. Reading `declarative` rows as a progress log leads to "it stopped
half-way through the alters", which cannot happen — stage 1 is all-or-nothing.

A failed publish records one row with `Succeeded = false` on the name `(publish)`,
best-effort. Redefine and migration failures likewise record a failure row for the
object or script that failed before the error propagates.

## Failures before anything runs

Not every failure leaves the database in a partial state — most do not touch it at all:

| Failure | Database |
|---|---|
| Desired-state files invalid or unparseable | Untouched — files are validated first |
| Programmable-object analysis fails (e.g. a dependency cycle) | Untouched |
| `--expect-plan` mismatch | Untouched — the gate runs before execution |
| The comparison could not read the target | Untouched — apply refuses ([errors.md](errors.md#an-incomplete-comparison-is-a-failure-not-a-partial-answer)) |

The ledger table is created before any of this, so failures are recordable too. It is
excluded from comparison, so creating it never shows up in a plan.

## See also

- [ADR-0004](adr/0004-failure-semantics-and-resume.md) — the decisions behind this, and why resume is convergent re-run rather than a repair command
- [ADR-0002](adr/0002-three-strategies.md) — why the three stages exist and what routes to each
- [errors.md](errors.md) — error codes and the message vocabulary
