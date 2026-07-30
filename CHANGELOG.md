# Changelog

Notable changes per release. Versions are `0.x` — the surface is still settling, and
minor versions may adjust behaviour where it was wrong. Machine contracts (the plan
format, the error envelope, exit codes, the CLI manifest) are versioned separately and
change **additively**: consumers must ignore properties they do not know.

## 0.6.0 — 2026-07-30

### Fixed

- **Removing a column's generation expression no longer discards its values**
  (PostgreSQL). Both directions of a generation-expression change were synthesized the
  same way — drop the column, add it back — on the reasoning that a generated column's
  contents are derived and therefore reproducible. That reasoning inverts at exactly the
  point this change is made: once the desired state removes the expression, the values
  become ordinary data, and the expression that could recompute them is the thing being
  removed. So an apply that was asked to *keep* a column and stop computing it returned
  every row of it as `NULL`, with no warning, on a change whose plan reads as an ordinary
  in-place alter. Dropping an expression is now `ALTER COLUMN … DROP EXPRESSION`, which the
  engine performs in place and losslessly, and a type, default or `NOT NULL` difference on
  the same column still gets its own statement. The other direction is unchanged and
  remains a rebuild — there is no in-place form for it on the supported baseline, and the
  new values are the expression's output by definition. Pinned by a live test that loads
  rows, converts, and asserts both the retained values and an empty re-diff.

- **Adding a column to an existing table no longer drifts forever on PostgreSQL.**
  The engine appends a new column whatever order the desired state lists it in, so a
  file that places it ahead of trailing audit columns can never match the live table
  positionally — and the comparison was treating ordinal position as state. Every
  `diff` after the `apply` re-proposed the same table: the plan never emptied, `status`
  never came clean, and any convergence gate built on them stayed red on a database
  that was in fact correct. Column order is now excluded from the comparison, which is
  the policy the project has stated since 0.3.0 — *Schemorph diffs state, not ordinal
  position* — and which the SQL Server provider already implemented; the second
  provider had not inherited it. Documented in [limitations.md](docs/limitations.md),
  including what to do when column position is material to an application.
- **A PostgreSQL `apply` no longer reports changes it did not execute.** The applied
  list was computed from the comparison before any DDL was synthesized and returned
  regardless of whether a statement existed, so a change the synthesizer could not
  express was reported as applied — and recorded in the history ledger as a success
  row, for work that never happened. The provider now checks its own two halves
  against each other: every synthesized statement carries the table it belongs to, and
  a change no statement carries fails the operation with **`SCHEMORPH009`**, naming
  the change. Nothing is applied and no plan is emitted, so a caller cannot mistake it
  for convergence. The check is per object rather than a count, so a gap beside a
  change that *did* synthesize is caught too. Deliberately provider-local: on SQL
  Server the update script is a review artifact and the publish is the execution path,
  so a missing script there does not mean nothing ran.
- **`diff --format sql` no longer blames a diagnostic that cannot have fired.** Its
  refusal asserted `SCHEMORPH002` — a code only the SQL Server provider emits — so on
  another provider it named a script-generation failure that never occurred while
  hiding the real cause. The refusal now quotes the diagnostic codes the plan actually
  carries, and says plainly when the plan carries none. Which diagnostic explains a
  missing script is the provider's to report; the core no longer asserts one (the same
  correction `SCHEMORPH008` needed in 0.3.1).

### Added

- **PostgreSQL plans explain themselves, and the safety lint has something to read.**
  `changes[].sql` was reserved as `null` on this provider, so a plan named the objects
  it would change without showing the DDL for any of them — and the two lint rules that
  read those slices (`SCHEMORPH101`, `SCHEMORPH102`) could never fire, including on the
  real hazard of adding a `NOT NULL` column with no default to a table that already
  holds rows. The provider now attributes every synthesized statement to the object it
  belongs to as it emits it, so attribution is exact rather than recovered by parsing
  the script back. The `NOT NULL`-without-default judgment is made on the model, not on
  the emitted text: it fires only for a table that already exists, and never for
  identity or generated columns, where the engine supplies the value. A table rebuild
  is never claimed — this provider alters in place and has no rebuild path.
- **`SCHEMORPH107` — a plan says when a column is re-created rather than altered.** A
  change is planned per object, so a table entry reads as one in-place alter even when
  carrying out the request means dropping a column and adding it back; the table and its
  other columns survive, that column's values do not, and nothing in the plan said so.
  The new lint code says it, in the same warning band and on the same terms as the rest:
  judged on the model rather than on the emitted text, so it fires only where it is
  proven, and it never changes the exit code. On PostgreSQL one shape reaches it — a
  column that gains a generation expression or changes the one it has, which has no
  in-place form on the supported baseline. Losing an expression is performed in place and
  keeps every value, so it deliberately does not warn. Providers that never re-create a
  column leave the signal off, exactly as `SCHEMORPH102`'s table rebuild is off where
  there is no rebuild path.
- **Plan format 1.5** — `changes[].sql` populated on every provider (additive; the field
  has existed since 1.0). Two consequences are documented in
  [plan-format.md](docs/plan-format.md): a plan can now carry lint warnings it did not
  carry before, and every plan hashes to a new value (the slice is bound by `planHash`
  since 1.4, and the fingerprint's delimiters changed — see below). Earlier hashes
  fail closed.

### Changed

- **The apply gate's fingerprint can no longer have its boundaries moved by the content
  it hashes.** `planHash` is a SHA-256 over the plan's shape and the executed script, and
  the parts were joined with a pipe between an action's members and a newline between
  actions — two characters SQL text holds routinely, since a change's DDL slice is
  multi-line and may contain any printable character. Where a delimiter can occur inside
  the data, it is the content that decides where one field ends and the next begins, so
  two materially different plans can in principle reach the same input string and
  therefore the same hash — and this hash exists precisely so that a reviewer's signature
  cannot be transferred to a different apply. The boundary between the shape and the
  script was already delimited by a character neither input can contain; the same
  treatment now applies inside the shape (`US` between an action's members, `RS` between
  actions), so every boundary comes from the encoding rather than from the data. Pinned by
  tests that construct two plans whose old encodings collided. No plan property changed,
  but the same plan hashes to a different value than it did in 0.5.2 — a hash captured
  earlier fails closed, which is the designed direction.

- **[plan-format.md](docs/plan-format.md) no longer understates what `planHash` covers.**
  It described `changes[].sql` as "excluded from `planHash`", which stopped being true in
  0.5.2 when the fingerprint began binding the executed script. A consumer reading it
  would have believed an attributed slice could change without invalidating a signed
  hash — the exact false assurance the fingerprint exists to remove. The field row and
  the `planHash` row now both state that every change's `sql` is bound, whatever its kind.
- **A refusal no longer points at a label the reader cannot look up.** Two PostgreSQL
  refusals ended with an internal development-plan label — *index changes (…) — slice P2*,
  *programmable objects (…) — slice P3* — which named nothing a user of the released tool
  can find, in the one message whose whole job is to say what is and is not handled. The
  refusals now name the unhandled work and stop there; what *is* handled still arrives
  with them, from the declared-capability hint that has always accompanied them. The same
  sweep removed internal-only references from source comments and test names throughout,
  so what ships explains itself from what ships.
- **[limitations.md](docs/limitations.md) describes two providers instead of one.** It
  still said SQL Server was the only engine and PostgreSQL a plan, which stopped being
  true at 0.5.0. It now states the declared PostgreSQL scope, that everything outside
  it is refused rather than half-planned, and that parity means an identical contract —
  not identical limitations.

## 0.5.2 — 2026-07-24

### Fixed

- **The apply gate now binds what executes, not just an object-level summary of it.**
  `planHash` hashed each change's name, type, operation and risk — but a change is
  planned per object, so two plans that alter the same objects with the same operation
  and risk but **different DDL** (a column added vs. only a constraint re-added) shared a
  hash. A reviewer could sign one plan's hash and `apply --expect-plan` would pass a
  materially different apply. The fingerprint now also covers the executed script — the
  declarative update script and each re-definition's SQL. The exposure was widest on the
  Postgres provider (its per-change `sql` is null), but the collision was
  provider-agnostic; both are fixed.
- **Plan format 1.4** — `planHash` now includes the executed script text (additive: no
  JSON field changed, but the same plan hashes to a new value). A hash captured under an
  earlier format no longer matches and the apply refuses rather than running unreviewed
  DDL — it fails closed. The script text itself is not embedded in the plan JSON;
  reviewers read it through the review script / `diff --format sql`.
- **The apply now populates the executed script for both providers, and the two
  operations generate it the same way.** The SQL Server provider previously handed the
  apply gate no script (only the object-level shape); it now generates the script and
  its per-change attribution the same way `diff` does, so the gate compares like for like.
- **The migration ledger table is created after the comparison, not before it** (SQL
  Server). With `planHash` binding the script, a ledger table present in the target but
  absent from the desired state made the generated script carry a spurious `DROP`,
  diverging from the pristine target `diff` compared and tripping the gate on an
  unchanged plan. Initializing it after the comparison keeps apply's target identical to
  diff's; ledger recording is unaffected (the table exists before any row is written).

## 0.5.1 — 2026-07-23

### Fixed

- **Postgres `CHECK (col IN (...))` on a `varchar` column no longer drifts forever.**
  The catalog re-renders such a constraint into a cast chain that reaches its fixed point
  only in the second generation; the shadow pipeline read the desired side one generation
  short, so every `diff` after an `apply` re-proposed the same constraint and `status`
  never came clean. The shadow now normalizes CHECK constraints to the engine's fixed
  point, so declarative loops with enum-style CHECKs converge. (`text` columns were never
  affected — the cast chain is varchar-only.)

## 0.5.0 — 2026-07-22

### Added

- **PostgreSQL provider — first slice: the table core.** `SCHEMORPH_PROVIDER=postgres`
  selects it (default stays `sqlserver`; existing consumers are unaffected). Declared
  capabilities: `inspect`, `tables`, `columns`, `constraints`, `schemas` — inspect,
  diff and apply over those, with the declarative apply running as **one tool-owned
  transaction** (applied entirely or not at all). Everything outside the declared list
  is refused with an explicit error naming what the provider does support, rather than
  half-planned. Comparison is native `pg_catalog` with shadow-schema normalization
  (ADR-0007); the migration ledger lives inside the target schema. A database-owner
  role with `CREATE SCHEMA` suffices — no superuser, no `CREATEDB`, no extensions.
- **Plan format 1.3** — a plan now declares its `atomicity` (`transactional` |
  `partial`; additive, excluded from `planHash`). **CLI manifest 1.4** — a `provider`
  block carries the active provider's name and capability list.
- CI runs the Postgres provider tests against a live `postgres:16` container under a
  deliberately restricted role (`NOSUPERUSER NOCREATEDB`), so the permission baseline
  above is exercised on every commit, not assumed.

### Changed

- Self-contained binaries grow by the Postgres stack: win-x64 measured
  188,877,831 bytes vs 181,835,572 in 0.4.0 (+7.0 MB, +3.9%; Npgsql + the pinned
  `pgsqlparser` native used for shadow rewriting).

## 0.4.0 — 2026-07-21

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
