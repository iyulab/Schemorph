# Recipe: a human approval gate on schema changes

Many teams require a person to read the DDL before it touches production. This
recipe makes that review **binding**: the reviewer signs one document, and the
apply refuses to execute anything that is not the plan in it.

Three commands:

```bash
# 1. Produce the artifact. Read-only — nothing is touched.
schemorph diff --schema ./schema --format sql > plan-2026-07-21.sql

# 2. A person reads it and (however your process records approval) signs it.
#    Archive the file: it is the record of what was approved.

# 3. Apply, gated on the fingerprint printed in that file's header.
schemorph apply --schema ./schema --expect-plan 4f1c9a2e…
```

`SCHEMORPH_URL` carries the connection string, so it stays out of shell history
and out of the reviewed document (the header shows the target with the password
redacted).

## Why the fingerprint is the point

Reviewing SQL and then applying is only a real gate if the two are the same
change. Between step 2 and step 3 someone can edit the schema files, or the
database can drift — and a plain `apply` would happily execute the *new* plan
against the *old* approval.

`--expect-plan` closes that: the apply computes its plan in the same comparison
session it would execute, compares the fingerprint, and aborts with
`plan_mismatch` **before anything runs** if it differs. The hash in the document's
header is that fingerprint. So the paper a human signed and the gate a machine
enforces are one artifact.

When it does abort, the honest response is to go back to step 1 — a new plan
needs a new review, which is exactly what the mismatch is telling you.

## Why the document is read-only

The header says "do not execute this file", and that is not caution — it is
correctness. Running the text with `sqlcmd` or SSMS would skip:

- the **history ledger**, which is the audit trail and the basis of every
  run-once guarantee;
- the **re-definition ordering**, which is computed from the dependency graph;
- the **migration run-once contract** — migrations are deliberately *not* in this
  document, because they are versioned files reviewed in the repository, not
  regenerated per plan.

The document exists so a person can read exactly what will run. Running it
directly makes the tool's own record of the change wrong.

## What the document contains

- A header: `planHash`, generation time, the redacted target, the apply's
  declared `atomicity` (what a partial failure leaves behind), the copy-paste
  `apply --expect-plan` command, and the read-only warning.
- Any plan messages, including the safety-lint band (`SCHEMORPH1xx`) — a reviewer
  sees the same warnings an automated policy gate would.
- **Stage 1** — the declarative publish, as the engine's own update script,
  verbatim. Not a reassembly of the plan's per-change slices: the text reviewed is
  the text executed. Each change is listed above it, with destructive ones marked.
- **Stage 2** — each idempotent re-definition, in dependency order, verbatim.

If the engine cannot generate the declarative script (`SCHEMORPH002`), the command
**fails** with `review_script_unavailable` instead of emitting a document that
silently omits a stage. An approval artifact missing changes is worse than no
artifact: it gets signed anyway.

## Pairing it with CI

The [plan-on-PR recipe](github-actions-plan-comment.md) posts the machine-readable
plan on every schema pull request; this recipe is what the release step does with
the approved one. A workable split:

| Stage | Command | Who |
|---|---|---|
| Pull request | `diff --format json` → PR comment, lint-code policy gate | CI |
| Pre-deploy | `diff --format sql` → artifact, archived | CI |
| Approval | read the artifact, sign it | a person |
| Deploy | `apply --expect-plan <hash from the artifact>` | CI |

Store the artifact wherever your change record lives. The `planHash` is what ties
that record to what the database actually did.

## If the apply fails anyway

A gate does not make execution infallible. See
[When an apply fails](../failure-semantics.md) — the failure names the stage it
stopped in and what had already committed, and re-running the same command is the
recovery path.
