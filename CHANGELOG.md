# Changelog

Notable changes per release. Versions are `0.x` — the surface is still settling, and
minor versions may adjust behaviour where it was wrong. Machine contracts (the plan
format, the error envelope, exit codes, the CLI manifest) are versioned separately and
change **additively**: consumers must ignore properties they do not know.

## Unreleased

### Fixed

- **Removing a column from the desired state is a destructive change, and is gated
  like one.** A plan is built per object, so a column the files stop declaring
  arrives as an `ALTER` of the table that holds it — the same shape as adding a
  default or widening a type. Risk was classified from that shape alone, so the
  most common way to lose data got the ordinary alter's `warning`, was included in
  every plan by default, and a plain `apply` carried it out. Every row of that
  column was gone, with `--allow-destructive` never asked for and no
  `SCHEMORPH1xx` fired. The gate stood only in front of a `DROP` of a whole
  object, which is the rarer half of the same hazard.

  The criterion has always been the loss rather than the syntax; what was missing
  is that a change's *object and operation* cannot express it. Providers now
  report whether a change removes a column, and the classification reads that
  alongside the operation. A column that is **re-created** is deliberately not
  gated — its new values are the new definition's output, so they are replaced
  rather than lost, and `SCHEMORPH107` describes it as before. The line is
  recoverability.

  **Read this before upgrading.** A change that applied on 0.8.0 can now be
  refused: `diff` reports no actions, `hasDestructiveChanges` stays `false`
  because nothing destructive is *in* the plan, and `SCHEMORPH001` says what was
  held back and why. The change is in `excluded[]` and in the review script.
  Passing `--allow-destructive` restores exactly the previous behaviour, now with
  `risk: "destructive"` and `SCHEMORPH103` on the entry. Gating is per object, so
  a safe change to the same table waits with the unsafe one rather than applying
  beside it — the plan and the apply must contain the same thing.

  **Both providers.** The signal is a dialect judgment and each provider proves it
  from its own comparison rather than from the generated text — PostgreSQL from the
  compared model, SQL Server from the comparison tree, where a removal arrives as a
  `Delete` on the column beneath a change to its table. Reading the script instead
  would make the classification depend on how a generator worded a statement, which
  is not what decides whether rows survive. Both exclude a column dropped and
  re-added under the same name, and neither gates a redefinition: the line is
  recoverability, and it is drawn once.

### Added

- **[limitations.md](docs/limitations.md) states what a rename becomes.** Objects are
  matched by name, so a renamed one is planned as a drop beside a create and no rename
  statement is emitted — true on both engines since the first release, and the one entry
  that page was missing. What makes it worth a section is that the shape arrives while
  the values do not: the row survives and the column carries the requested name holding
  nothing, so every check short of reading a value agrees the rename worked.

  The column gate above is what keeps it from being quiet, and it is why the two
  entries are worth reading together: a rename is the easiest way to reach a column
  removal by accident, since it does not look like removing anything. Both engines now
  refuse it and say so. Also documented: why inferring the rename from shape would be
  worse than refusing to — a wrong guess lands data in a column it does not belong to,
  which nothing downstream reports — and the order of operations that keeps the values
  (`RenameTests`, both providers).

- **`SCHEMORPH108` — a plan says when it drops an index the desired state does not
  declare.** Since 0.7.0 an undeclared index is planned away, and correctly so:
  the files are the whole truth about a table's indexes. It is also correctly
  **not** gated — an index holds no data of its own, which 0.7.0 measured on both
  engines. But the lint band said nothing either, and that silence is read. A
  reviewer who has learned that this band speaks up when something is at stake
  sees no warning and concludes there is nothing to lose, while the queries that
  index answered fall back to a scan.

  Saying it required widening what the band is about, from *data that does not
  survive* to **cost the apply changes** — data loss being the most expensive kind
  rather than the only one. Nothing about gating moved: what fires here and what
  the destructive gate stops remain separate questions, and this fires on a change
  that runs by default. Upgraders gating CI on the `SCHEMORPH1xx` band (the shape
  the [plan-comment recipe](docs/recipes/github-actions-plan-comment.md) suggests)
  can fail on a plan that passed before, without the plan itself having changed.

### Changed

- **`planHash` moves for any plan containing a change that removes a column.**
  Risk is part of what the fingerprint binds, and that change's risk is now
  `destructive`. A hash captured under 0.8.0 no longer matches and the apply
  refuses rather than running unreviewed DDL — fail-closed, the designed
  direction. No plan property changed and the format version is unchanged: this
  is a classification correction, not a contract addition.

## 0.8.0 — 2026-08-03

### Fixed

- **The review document now says which of its statements do not run.** The document is
  the engine's own update script, reproduced exactly so that what a person reviews and
  what an apply executes are one artifact. That fidelity has a consequence nobody was
  told about: the engine writes the script over the whole comparison, so it can contain
  DDL for objects the plan drops — and reading a review document, a statement means
  "this runs".

  Two kinds of object land there. Schemorph's history ledger is compared like any other
  table and is not part of the desired state, so every run against a target that already
  has one — that is, every target after the first apply — produced a `DROP TABLE` for it
  in the reviewed text. It was never executed, and the plan, the change count and the
  messages all stayed silent about it, because the ledger is deliberately invisible in
  user-facing output. Destructive changes gated out of a plan are the same shape: warned
  about, dropped from execution, still present in the script.

  A reviewer had no way to tell any of that from the text they were signing. The header
  now names those objects above the script, with why each one does not execute, and
  bounds itself explicitly — anything not listed does run. Naming them matters more than
  the false stop it prevents: an operator who learns that a `DROP` here is inert will
  wave through the one that is not.

### Changed

- **Plan format 1.6 — `excluded[]`.** The same fact on the machine path: automation
  reading `changes` and `hasDestructiveChanges` could not see those statements either,
  since both describe what executes. The field is always present and empty when a plan
  runs everything its script contains. **`planHash` does not change** — it is derived
  from the actions and script the hash already binds, and a plan's identity must not
  move because it started explaining itself better, so a hash captured under 1.5 still
  matches. See [docs/plan-format.md](docs/plan-format.md).

## 0.7.0 — 2026-07-30

### Added

- **Indexes are declared state on PostgreSQL.** The provider plans and applies index
  additions, removals and redefinitions, and `indexes` joins its declared capability
  list; an index difference no longer refuses. An index change is reported as a change
  to the table it is on, which is how the SQL Server provider has always reported it —
  the two plans read the same. Because the desired state is now the whole truth about a
  table's indexes, **an index the files do not declare is dropped**, and appears as a
  `DROP INDEX` in the review script like any other planned statement. Indexes a
  constraint owns stay out of it: they belong to the constraint that creates them.

  **That drop is not gated by `--allow-destructive`**, on either engine — measured, not
  assumed. An index holds no data of its own, so removing one classifies as an ordinary
  alter, the same classification the SQL Server provider has always given it. Read the
  consequence before the first apply: a repository that was *refused* on 0.6.0 because
  the live database carried an index the files never mentioned now plans that index
  away, and the default `apply` will carry it out. `diff --format sql` shows the
  `DROP INDEX` before anything runs, and that review script is the place to catch it.
  No `SCHEMORPH1xx` fires — the band is about data that does not survive, and this is
  query time rather than rows.

  Foreign keys are the case worth naming, because PostgreSQL creates no index for one on
  its own. A foreign-key column without a supporting index is a table scan on every
  lookup through that key, and until now the provider could not be asked to fix it.

### Fixed

- **An index whose definition drifted could be reported as no change at all**
  (PostgreSQL). The comparison identified an index by a projection over its columns,
  built from `pg_get_indexdef(oid, n, …)` — and that rendering carries only the bare
  column or expression. Sort direction, `NULLS FIRST`/`LAST`, operator class and
  collation are all absent from it, whatever the pretty flag says. Two indexes differing
  only there were therefore equal to the tool: `diff` printed no changes, `status` came
  clean, and the difference stayed in the database with nothing left to plan it away. It
  is not a cosmetic set: an index is a set of columns *under an ordering*, and the four
  aspects decide which queries the index can answer. The comparison now takes the
  engine's whole `CREATE INDEX` rendering, exactly as constraint definitions are taken,
  with the one qualifier that differs between two schemas removed from its `ON` clause.
  Pinned by live tests that seed each aspect on one side only and require the plan to
  see it and then converge.

### Changed

- **`CREATE INDEX CONCURRENTLY` in a desired state is refused** (PostgreSQL,
  `not_implemented`) rather than silently serialized. A concurrent build cannot run
  inside a transaction, and a PostgreSQL apply is one transaction the tool owns — the
  guarantee it declares as `transactional`. Honoring the keyword would drop one of the
  two without saying which, and both are the caller's to trade. See
  [limitations.md](docs/limitations.md) for the two ways through it, and
  [ADR-0007](docs/adr/0007-postgres-engine-selection.md)'s 2026-07-30 addendum for why
  marking the plan is not yet an option.

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
  there is no rebuild path. Worth stating for upgraders: a plan can carry this warning
  where it previously carried none, so a pipeline gating on the `SCHEMORPH1xx` band —
  the shape the [plan-comment recipe](docs/recipes/github-actions-plan-comment.md)
  suggests — can fail on a change that passed before. That is the warning doing its job,
  but it arrives without the plan itself having changed.
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
