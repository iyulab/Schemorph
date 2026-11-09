# ADR-0009: An opt-in online stage after the apply commits

- **Status:** Accepted (design; not yet implemented — the behavior below is what the implementation must meet)
- **Date:** 2026-11

## Context

On PostgreSQL an apply is one transaction the tool owns ([ADR-0004](./0004-failure-semantics-and-resume.md), 2026-08-20 addendum): the declarative publish, every redefinition and every migration land together or not at all. That guarantee is the reason two kinds of work are refused today:

- **`CREATE INDEX CONCURRENTLY`** cannot run inside a transaction. A desired state containing it is refused, and so is a migration containing it (`SCHEMORPH109` is fatal under `atomicity: transactional`). The documented way out is to build the index by hand outside the tool and declare it plainly afterwards; the next diff finds it and plans nothing.
- **Adding a large constraint without a long lock** — `ADD CONSTRAINT … NOT VALID` followed by `VALIDATE CONSTRAINT` — is not refused, but it does not help inside one transaction. Both statements may run in a transaction; the benefit of the split is that the expensive scan happens under a weaker lock **after** the strong lock is released, and a lock acquired in a transaction is held until that transaction ends. Inside a single apply-wide transaction the strong lock lives as long as the whole apply.

Both come from the same fact: the apply has no commit boundary inside it. Giving up the all-or-nothing guarantee to fix that would trade a property every PostgreSQL user of the tool relies on for a property only some of them need.

## Decision

1. **Add a fourth stage, `online`, that runs after the transactional stages (1–3) have committed.** Stages 1–3 keep their contract exactly: the apply lands whole or not at all. Only the online stage is allowed to run statements that cannot join that transaction.

2. **What moves into it.** A concurrent index build replaces the plain `CREATE INDEX` for an index the plan creates; for a constraint the plan adds, stage 1 adds it `NOT VALID` (a short, metadata-only lock) and the online stage runs `VALIDATE CONSTRAINT`. Each online statement runs on its own and commits on its own.

3. **It is opt-in, per apply.** A command-line option and its MCP equivalent turn it on; without it the plan and the apply are byte-for-byte what they are today. It is not chosen by table size or any other measurement of the live database — a plan whose shape depends on statistics is not reviewable, and review is the point of `diff` → `apply --expect-plan`.

4. **The desired state stays declarative.** `CONCURRENTLY` in a desired-state file remains refused: a file states what should exist, and *how* to build it is a property of the apply, not of the state. The plan is rewritten at plan time, so the online statements appear in the reviewed script and are covered by `planHash` like every other executed statement.

5. **The plan says which statements are online.** The `atomicity` of stages 1–3 is still `transactional`; online steps carry their own marker and are listed after the transactional ones, so a reader can tell what lands atomically and what does not without knowing the engine.

6. **Failure and resume stay convergent.** A failed online statement is reported as stage `online` with the count of online statements already committed, and earlier stages stay committed (they already were). Re-running the same apply must converge, which requires the tool to see what a failed online statement leaves behind:
   - a failed concurrent build leaves an **invalid index**; it is treated as drift, and the next plan drops and rebuilds it (concurrently);
   - a constraint added `NOT VALID` and never validated is treated as drift from the declared (validated) constraint, and the next plan is the `VALIDATE` alone.

   **Prerequisite:** the inspector does not read index validity or constraint validation today. That must be implemented, and proven against a real server with injected failures, before the online stage is.

7. **A lock timeout applies to each online statement** when one is configured, so an online statement that cannot get its lock fails and can be re-run instead of waiting indefinitely.

8. **Out of scope:** SQL Server (its online options are a separate decision) and any other non-transactional statement. The stage is a fixed place for these two operations, not a general mechanism for arbitrary non-transactional work.

## Alternatives considered

- **Keep refusing, document the out-of-band path.** Correct and simple; leaves every user with a large table doing the work by hand and the plan unable to describe it. Rejected as the long-term answer; it remains the behavior until this ADR is implemented.
- **Plans carry arbitrary commit groups.** A general mechanism: any boundary, any number. It widens the plan format and the failure semantics (what is committed when group *k* fails) far beyond what the two known operations need. Revisit if a third kind of non-transactional work shows up that does not fit a single trailing stage.
- **Choose automatically from table size.** Makes the reviewed plan depend on live statistics and on when the plan was computed. Rejected.
- **A `CONCURRENTLY` keyword accepted in desired-state files.** Puts an apply strategy into the declarative state and makes one file mean two different guarantees depending on a keyword. Rejected (decision 4).

## Consequences

- The default apply is unchanged; nobody pays for this unless they ask.
- The plan format gains the online marker (additive) and the error envelope gains the stage `online`.
- `docs/failure-semantics.md` gains the fourth stage and the statement of what it does and does not guarantee; `docs/limitations.md` replaces the "build it by hand" workaround with the option once it exists.
- A design that lets an apply end with an invalid index or an unvalidated constraint is only safe because the next run sees it; decision 6 is therefore the gate on the whole feature, not a follow-up.
