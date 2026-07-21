# Changelog

Notable changes per release. Versions are `0.x` — the surface is still settling, and
minor versions may adjust behaviour where it was wrong. Machine contracts (the plan
format, the error envelope, exit codes, the CLI manifest) are versioned separately and
change **additively**: consumers must ignore properties they do not know.

## Unreleased

Recommended next version: **0.4.0** (minor — new CLI output format and new error codes).

### Added

- **`diff --format sql`** — the whole plan as one review document a person can read and
  sign, in execution order, with the `planHash` in its header. The declarative stage is
  the engine's own update script verbatim and re-definitions follow each verbatim, so the
  text reviewed is the text executed rather than a reassembly. Read-only by design: the
  header points at `apply --expect-plan <hash>`, because running the file with a SQL
  client would skip the history ledger, the re-definition ordering and the migration
  run-once contract. Migrations are deliberately not included — they are run-once files
  reviewed in the repository, not regenerated per plan. New recipe:
  [human approval gate](docs/recipes/human-approval-gate.md).
- **A failed `apply` reports where it stopped** — the error envelope carries `stage` and
  `committed{declarative, redefines, migrations}`. Apply runs three strategies in order
  and does not roll back across them, so "where it stopped" is the difference between a
  database that changed and one that did not. Counts rather than names: the ledger
  remains the per-object record, and this exists so a caller need not query it to learn
  whether anything changed at all.
- New error codes: `redefine_execution_failed`, `migration_execution_failed` (a script
  failing against the database — distinct from the same-named `invalid_state` codes,
  which are desired-state problems found before those stages run anything),
  `review_script_unavailable`, and `temp_workspace_unavailable`. CLI manifest 1.3.

### Fixed

- **Failure hints no longer guess.** Several verbs paired `catch (Exception)` with a
  fixed hint, so an `inspect` failure blamed "the connection string and output
  directory" when neither was the cause. Hints now follow the exception, and where no
  cause has been established the hint is **absent** rather than confidently wrong.
- **`--format json` writes exactly one JSON object to stderr on failure**, as
  [docs/errors.md](docs/errors.md) always claimed. The failure paths were echoing
  `[Error] …` lines ahead of the envelope, and the hint "See messages above" pointed at
  messages the JSON did not contain. Applies to `apply`, `diff` and `status`.
- **Intermediate files are the tool's own responsibility.** Schemorph keeps working
  files under `<temp>/schemorph`, creates that directory before use, and reports a
  failure to create it as `temp_workspace_unavailable` — naming the directory and the
  `TMP`/`TEMP` variable rather than an internal `.dacpac` filename the caller never
  asked for. Cleanup can no longer throw over the failure it is cleaning up after
  (`File.Delete` raises when the directory is gone, which is exactly when something has
  already gone wrong).
- **`--format sql` is refused where it means nothing.** Only `diff` produces a review
  document; the other verbs used to accept the flag and quietly render text, which is the
  tool advertising an output it cannot produce.
- **Every MCP tool answers a failure with the same envelope.** `schemorph_diff` and
  `schemorph_inspect` had no error handling at all, so their failures surfaced in the
  MCP framework's shape instead of Schemorph's; `schemorph_status` and `schemorph_apply`
  mapped only some exception types. All four are uniform now.

## 0.3.1 — 2026-07-21

### Fixed

- **`SCHEMORPH008` fires on the effect, not a guessed cause.** It was keyed on the
  engine reporting a missing server-scope `VIEW ANY DEFINITION`, so it fired on every
  least-privilege connection this project's own guidance recommends — including ones
  that read the target completely. Four logins against one database refuted both the
  premise and the two fixes proposed for it: a `db_owner` without the server grant reads
  everything, and a login *granted* `VIEW DEFINITION` at database scope but denied on the
  changed object comes back empty, so keying on that permission would have traded a false
  positive for a false negative in a safety warning. The engine does not omit silently
  either — it reports an error in exactly the incomplete cases — so that error is the
  trigger, and the message echoes the engine's own reason instead of asserting one.

### Added

- **[docs/failure-semantics.md](docs/failure-semantics.md)** — what the database looks
  like when an apply fails partway, why re-running is the only safe resume, and how to
  read the ledger. Ledger rows mean different things per kind: `declarative` rows are
  written in one batch *after* the publish commits, so a plan with 8 declarative changes
  shows 8 rows or 0, never 3. Reachable from `apply --help`, the `schema` manifest and
  the README.

### Changed

- A top-level `comparisonIncomplete` plan flag was built and then removed as unreachable:
  an incomplete comparison never becomes a plan, because `diff` fails and `apply`
  refuses. Plan format stays **1.2**.
- The integration suite is deterministic: its own parallelism was the load the engine was
  buckling under.

## 0.3.0 — 2026-07-14

### Changed

- **Security principals are outside the declarative model**
  ([ADR-0006](docs/adr/0006-security-principals-out-of-declarative-model.md)) — a
  code-generated desired state never emits users, logins, roles or permissions, so
  comparing them proposed dropping live principals for being "absent". They are excluded
  from the comparison instead.
- **Column order is not compared** — Schemorph diffs state, not ordinal position, so
  adding a column mid-table stays an in-place `ALTER ADD` rather than reading as a full
  table rebuild.

### Added

- `SCHEMORPH008`: a restricted comparison is surfaced rather than silently returning a
  partial result that reads as "in sync".

## 0.2.0 — 2026-07-12

### Added

- **Agent-native surface**: plan format 1.0 → 1.2 (`changes[].actions`, `planHash`,
  per-change explanations and attributed SQL), fingerprint-gated apply (`--expect-plan`),
  an MCP server (`schemorph mcp`) with tools and schema/plan resources, the `status`
  verb, the `SCHEMORPH1xx` safety-lint band, an Agent Skill, and a GitHub Actions recipe
  that posts the plan on schema pull requests.
- **Brownfield adoption**: existing databases and SSDT trees are consumed as-is —
  non-model files are classified out with warnings, and history-less programmable objects
  matching their live definitions are reconciled (recorded, nothing executed) rather than
  re-applied.

## 0.1.0 — 2026-07-12

First public release. `inspect` / `diff` / `apply` / `status` against SQL Server, with
the three-strategy model ([ADR-0002](docs/adr/0002-three-strategies.md)): structural
changes are diffed, programmable objects are re-applied idempotently via
`CREATE OR ALTER`, and data changes are versioned run-once migrations tracked in a
history ledger. Destructive-change gating, semantic exit codes, a typed error envelope,
and password redaction at every output sink.
