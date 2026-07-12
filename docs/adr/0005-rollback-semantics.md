# ADR-0005: Rollback semantics per strategy

- **Status:** Accepted (2026-07-12; both decision points resolved as recommended)
- **Date:** 2026-07

## Context

Every schema tool must answer "how do I undo an applied change?", and the answer is
strategy-specific. ADR-0002 gives Schemorph three strategies with different natural
undo stories; ADR-0004 already settles what happens on *failure* (per-unit transactions
roll back, re-run resumes) — this ADR covers undoing changes that **succeeded**.

The roadmap's framing applies: *"'Not supported' is an acceptable outcome, but it must
be an explicit, reasoned decision."*

Reference behavior elsewhere: Flyway ships `undo` migrations only in paid tiers and the
community norm is roll-forward; Atlas rolls a declarative schema back by applying the
previous desired state; EF Migrations generates `Down` methods that are widely
acknowledged to be untested-by-default and unreliable for data.

## Decision

### 1. Declarative and redefine strategies: rollback = roll forward to the previous state

The desired state lives in git. Undoing a structural or programmable change is:

```
git revert <commit>        # or checkout the previous state of schema/
schemorph diff  --url ... --schema ./schema     # review the reverse plan
schemorph apply --url ... --schema ./schema
```

The engine needs **no new capability**: the diff of "current database vs. previous
files" *is* the reverse migration, computed the same way as the forward one, with the
same destructive gating (a rollback that drops a data-bearing table demands
`--allow-destructive` like any other plan). Programmable objects re-apply their previous
definition via the same checksum/`CREATE OR ALTER` path.

No dedicated `rollback`/`undo` verb is added. A convenience wrapper (e.g.
`apply --to <git-ref>`) would duplicate what git already does better, and hiding the
git step hides exactly the review point (the plan) that makes rollback safe.

**Caveat, stated in docs:** a reverse *structural* diff restores shape, not data. A
reverted `DROP COLUMN` recreates an empty column. The plan shows this honestly (the
reverse operation is visible); recovering the data is a versioned-migration or backup
concern, never something the diff can promise.

### 2. Versioned data migrations: down migrations are not supported

Schemorph will not execute "down"/undo scripts for versioned migrations, deliberately:

- Data changes are generally not invertible (deleted rows, lossy transforms,
  `UPDATE`s that destroy prior values). A mechanical down-runner would execute
  hand-written inverse scripts whose correctness the tool cannot check — false
  confidence with the tool's name on it.
- Down scripts are, in practice, untested code that runs for the first time during an
  incident. The industry evidence (Flyway paywalling undo, roll-forward as the dominant
  operational norm) matches ADR-0002's anchor: *data changes are events, not state*.
- The remediation path is a **new forward migration** (`V(n+1)__revert_...sql`) written
  with knowledge of the actual incident, or a backup restore. The ledger's record of
  what ran and when is the input to writing that remediation correctly.

The ledger keeps run-once semantics: a reverted-by-new-migration change leaves both
migrations in history, which is the truthful audit trail.

### 3. Failed applies are not "rollback"

Undoing a *failed* apply is ADR-0004's job (per-unit transactions + convergent re-run)
and requires no user action beyond re-running. This ADR's scope is exclusively
successful changes the user has decided to withdraw.

## Decision points (resolved 2026-07-12)

1. **"No down migrations" is the documented contract** — *accepted* over optional
   `U####__*.sql` undo scripts (Flyway-style). Undo scripts would need their own ledger
   semantics (what does history mean after an undo?), their own testing story, and
   contradict the events-not-state anchor. Revisit only on concrete dogfooding demand
   with a real incident scenario.
2. **No dedicated rollback verb** — *accepted* over `apply --to <git-ref>` sugar; the
   git-revert workflow is documented instead. If agent-surface work (Phase 2) shows
   agents fumbling the two-step git dance, that is the demand signal to revisit.

## Consequences

**Positive**

- Rollback inherits every safety mechanism the forward path has (plan preview,
  destructive gating, ledger recording) instead of a parallel, less-tested path.
- No second migration dialect (undo scripts) to author, review, and keep correct.
- The message to users is one sentence: *"Rollback is applying the previous state;
  data changes roll forward."*

**Negative / accepted risks**

- Structural rollback cannot restore data (documented caveat above).
- Teams with a hard "every release must have a tested down script" policy will find
  Schemorph opinionated against them; that is a deliberate position, not an oversight.
- Point-in-time recovery remains a database-backup concern, out of scope.

## Alternatives considered

- **Generated down scripts for declarative changes** (EF-style): rejected — generating
  the reverse of a diff is exactly re-diffing against the old state, which decision 1
  already does without a second code path; generating them *ahead of time* freezes a
  reverse plan that may no longer match the database by the time it runs.
- **Undo migrations (`U####__*.sql`)**: rejected for Phase 1 (decision point 1).
- **Snapshot/restore integration** (automatic pre-apply backups): out of scope; belongs
  to operational tooling around the database, not the schema tool.
