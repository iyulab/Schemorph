# ADR-0007: PostgreSQL provider engine — native catalog comparison with shadow normalization

- **Status:** **Accepted** (2026-07-22) — accepted together with the open apply-atomicity
  question from [ADR-0003](./0003-postgres-as-second-provider.md)'s addendum, which is
  resolved below and carried into [ADR-0004](./0004-failure-semantics-and-resume.md)
- **Date:** 2026-07 (candidate survey and spikes run 2026-07-21; accepted 2026-07-22)

## Context

ADR-0003 deferred the Postgres engine choice until the work began, so that it would
be decided with the most information. The work has begun. This ADR resolves the
deferral the way ADR-0003 demanded: by measuring the candidates it named against the
committed consumer's behavioral requirements, not by reading feature lists.

The evaluation axes were fixed before the spikes ran: extension-freedom on managed,
non-superuser Postgres; *control* over apply atomicity (not merely its presence);
*control* over expression normalization (false re-diffs are this tool category's
chronic defect — SQL Server exhibits the same class today via DacFx text comparison);
single-file distribution; and compatibility with the plan-hash gate and review-script
artifacts.

Both spikes ran the same scenario on Postgres 16 as a `NOSUPERUSER` role: a four-table
schema with quoted PascalCase identifiers, uuid PKs, FK CASCADE, UNIQUE, and
`CHECK (... IN (...))` constraints — apply, re-diff (must be empty), apply a delta,
re-diff again (`spikes/pg-engine-selection/`).

## Decision (proposed)

Build the PostgreSQL provider on **native catalog comparison over Npgsql**, with
**shadow normalization**: the desired state is applied to a scratch database — or a
scratch schema in the same database where `CREATEDB` is unavailable — and both sides
are then introspected from `pg_catalog` in the engine's own canonical rendering
(`pg_get_constraintdef`, `pg_get_expr`, `format_type`). The engine itself is the
expression normalizer; the comparison layer contains no SQL parser. This is the
technique proven by migra and stripe/pg-schema-diff.

**Why not psqldef as a subprocess.** The spike, not the feature list, decided this:

- **Correctness on the acceptance schema.** psqldef 3.11.16 loses identifier quoting
  when synthesizing CHECK expressions (`"Tier"` becomes `Tier`, which Postgres folds
  to `tier` → error 42703) — its own generated DDL fails on any quoted mixed-case
  schema, which is exactly what ORM-generated schemas look like. Its *comparison*
  normalization passed; its *synthesis* did not, and synthesis is the layer a wrapper
  cannot reach into.
- **Atomicity without control.** psqldef wraps DDL in `BEGIN;…COMMIT;` and the spike
  confirmed a real whole-batch rollback on failure — but the transaction belongs to
  psqldef. Schemorph could not join its ledger write or plan-hash re-verification
  into it. The requirement was control, not the guarantee alone; native execution
  gives Schemorph the transaction.
- **Distribution.** A per-RID Go binary inside a dotnet tool, plus process management
  and version pinning, against zero added weight for managed code.

**What this choice costs, stated honestly.** Diff *detection* is spike-proven; DDL
*synthesis* (rendering the ALTERs) is not, and it is the expensive part — the reason
tools like migra ended deprecated. The vertical-slice plan puts expression-normalized
structural diff first precisely because it is the highest-risk slice. If synthesis
proves untenable, this ADR's alternative is re-evaluated — but with psqldef as a
synthesis *reference*, not necessarily as the engine.

A parser binding (`libpg_query` family) is **not adopted now**: shadow normalization
removed the need for parser-based expression comparison, and the .NET bindings'
maintenance level is unverified. It remains a candidate *component* (e.g. statement
splitting, fingerprinting) if the synthesis work wants one.

## Consequences

**Positive**

- Both control axes (atomicity, normalization) are held by Schemorph, measured rather
  than assumed. The plan-hash gate, ledger write, and apply can share one transaction.
- Transactional apply becomes an honest, declarable capability — the shape the open
  atomicity question (ADR-0003 addendum) needs in order to be decided as an explicit
  plan field rather than a leaked provider quirk. That decision remains open and is
  taken at acceptance of this ADR.
- No external binaries; the provider is managed code end to end.

**Negative / accepted risks**

- Schemorph owns a Postgres DDL synthesis layer — the largest and least automatable
  part of the provider. Mitigated by vertical slices ordered by risk, and by the
  scenario corpus (re-diff-empty invariants) pinning every slice.
- Shadow normalization requires a scratch database or schema; the same-database
  scratch-schema variant is spike-proven but its rewrite step must become parser-based
  (the spike used string substitution) and cross-schema references are untested.
- The catalog queries are version-sensitive surface area across Postgres majors; the
  integration suite must run against every supported major.

## The atomicity question, resolved (2026-07-22)

ADR-0003's addendum left one question for this ADR's acceptance: if Postgres can make an
apply atomic and SQL Server cannot, does the guarantee become a per-provider difference —
and how does that not violate "the user-facing contract must not leak provider specifics"?

**Resolved as declared capability, not silent divergence.** The plan and status envelopes
carry an explicit `atomicity` field: `transactional` when the whole apply is one unit that
either lands or does not, `partial` when it is not.

This is not a leak, because the field is provider-*agnostic* in shape: it is a statement
the tool makes about what it can guarantee for *this* run, in the core's own vocabulary.
A consumer reads one field rather than knowing which database it is talking to. The
alternative — flattening to the lowest common denominator — would discard a real property
of the target database and make the tool describe itself less accurately than it could.

The distinction is precise, and ADR-0004 already fixed both ends of it:

| | Scope of the guarantee |
|---|---|
| SQL Server → `partial` | Each stage is transactional (ADR-0004 §3), but the pipeline is fail-fast with **no cross-stage rollback** (§5). Wrapping the pipeline in one transaction was rejected there on the grounds that DacFx owns its own connection — so this is a settled property, not an omission |
| Postgres → `transactional` | The spike measured what makes this reachable: with native execution Schemorph owns the transaction, so the plan-hash re-verification, the DDL, and the ledger write can share it. Non-transactional exceptions (`CREATE INDEX CONCURRENTLY` and kin) are marked in the plan rather than silently degrading the claim |

The field must be *earned*, not asserted: a provider declares `transactional` only where
the tool holds the transaction boundary. This is why the guarantee was scored as
*control* over atomicity rather than its mere presence — psqldef had the rollback and
not the boundary.

## Addendum (2026-07-22): the parser question, answered by the work that needed it

The decision above deferred a parser binding ("not adopted now") on two grounds: shadow
normalization removed the need for parser-based *expression comparison*, and the .NET
bindings' maintenance level was unverified. Both grounds have since been tested by the
work the deferral pointed at.

The need arrived first, and it was measured rather than assumed. The scratch-schema
shadow variant requires retargeting desired-state DDL into the scratch schema, and string
substitution was refuted with a concrete counterexample before any parser was considered:
`pg_get_indexdef` renders fold-safe schema qualifiers **unquoted** (`src_test."Workspaces"`),
so quoted-form substitution misses them and the statement silently lands in the source
schema (the first hard evidence that the rewrite step must be parser-based,
exactly as this ADR's risk list anticipated).

The maintenance question was then answered by measurement (`spikes/pg-parser-rewrite/`):
**`pgsqlparser` 1.0.0** (libpg_query, PostgreSQL 17 grammar) parses the quoted-PascalCase
corpus shapes, exposes every schema-bearing position as a structured field (`RangeVar`,
FK `pktable`, qualified `TypeName`, qualified `funcname`), ships natives for exactly the
release RIDs (win-x64, linux-x64, osx-arm64) — and its **deparse preserves identifier
quoting**, the precise synthesis defect that eliminated psqldef.

**Adopted, narrowly**: `pgsqlparser` becomes the shadow harness's rewriter — AST rewrite
of the schema fields plus deparse, executed only against the scratch schema. Deparse
normalizes formatting, and that is acceptable *there specifically* because the shadow text
is never compared as text: both sides are read back from `pg_catalog` in the engine's own
canonical rendering. Verbatim remains the rule wherever text is the artifact (inspect
output, redefine scripts). The comparison layer still contains no parser.

Risk posture: exact version pin with corpus-gated upgrades — the DacFx policy, reused.
If the binding goes unmaintained, the replacement surface is three functions over a
pinned libpg_query (vendored P/Invoke), recorded in the spike README as the fallback.

## Addendum (2026-07-30): non-transactional index builds refuse rather than mark

The atomicity table above says non-transactional exceptions — `CREATE INDEX CONCURRENTLY`
and kin — are "marked in the plan rather than silently degrading the claim". The index
slice implements the first half of that sentence and not the second, and the reason is
that the second half has no place to live yet.

`atomicity` on a plan is copied from the provider's declaration; nothing computes it per
plan, and nothing splits an apply into a transactional part and a part that runs outside
the transaction. Emitting a *mark* on a plan whose `atomicity` still reads
`transactional` would be the degraded claim this ADR set out to prevent, only with a
note attached — and a reviewer who reads the field rather than the note gets exactly the
wrong answer about what a failure leaves behind.

So a desired state containing `CONCURRENTLY` is **refused**, typed
(`not_implemented`), naming the trade it will not make silently: the keyword avoids a
lock, the apply is one transaction the tool owns, and a concurrent build cannot join
one. Both are real and only the caller can choose between them. Marking becomes
implementable when a plan can carry an atomicity of its own; until then the refusal is
what this ADR's own standard requires — no plan the provider cannot stand behind.

## Addendum (2026-08-19): the `transactional` claim was never earned across the pipeline

The table above scoped Postgres → `transactional` from what the spike measured: a native
declarative publish can hold the transaction boundary Schemorph itself owns. True as far
as it goes — the declarative stage (strategy 1) is one tool-owned transaction, same as
this ADR always said. What the table elided is that `atomicity` (ADR-0004 addendum)
describes the *whole apply*, and the apply is three stages (ADR-0002), not one.
`ApplyOperation.RunAsync` — the Core orchestration both providers share — commits the
declarative publish, then runs redefines, then migrations, each stage its own connection
and its own transaction, with no boundary spanning them. That is a Core property, not
something either provider's implementation controls, and it was already true the day
this ADR was accepted; the table just never checked it against strategies 2 and 3.

The programmable-object slices (this project's cycles 110–111) made the gap concrete
rather than theoretical: `RedefineRunner` executes each redefinition through its own
`ExecuteScriptAsync` call, a fresh connection per object, same as `MigrationRunner` does
per script. A redefine failing after the declarative stage committed is not a corner
case — it is the ordinary shape of a partial apply, and it now has a live reproduction:
a table created, committed, and visible, while a view in the same apply fails to
redefine. **`atomicity: transactional` cannot describe that outcome** — the failure-
semantics contract for `transactional` is "lands whole or not at all", and this plainly
did not.

**Corrected: `PostgresProvider` declares `partial`**, the same guarantee scope SQL
Server declares, for the reason the table above already gave for SQL Server — each
stage's own transaction is real, but nothing wraps the stages together. This is not a
capability regression (the declarative stage is still one transaction; `CONCURRENTLY`
is still refused for exactly the reason given above, unchanged), only a corrected
declaration: `transactional` was asserted, not earned, once redefines and migrations
existed to make the pipeline more than one stage. No release ever shipped the
overclaim — cycles 110–111, which introduced the gap, were still unpushed local commits
when this addendum caught it.

Reclaiming `transactional` for real is a Core change, not a provider one: something
would need to hold a single connection/transaction across all three stages of
`ApplyOperation.RunAsync` for a provider that can support it, which the current
`IDatabaseProvider.ExecuteScriptAsync(connectionString, …)` shape — a string, not a
shared handle — does not allow. Left as a future direction, not scoped here.

## Addendum (2026-08-20): the future direction taken — `transactional` reclaimed

The Core change the 2026-08-19 addendum above described as "a future direction, not
scoped here" is done. Consequences §69–71 above wrote, at acceptance, that "the
plan-hash gate, ledger write, and apply can share one transaction" — a capability this
ADR scored the provider on, not yet a thing any provider had built. It is now literally
true for PostgreSQL: `ApplyOperation.RunAsync` (Core) threads a single provider-owned
session across the declarative publish, every redefine, and every migration of one
apply, committing once at the end or rolling every stage back together on the first
failure.

The mechanism — `IApplySession`, `BeginApplySessionAsync`, and how it closes the gap
the 2026-08-19 addendum measured — is recorded at
[ADR-0004's 2026-08-20 addendum](0004-failure-semantics-and-resume.md#addendum-2026-08-20-postgresql-earns-transactional),
not repeated here. **`PostgresProvider` again declares `transactional`**, this time
earned across the whole pipeline rather than asserted from the declarative stage alone.
SQL Server's `partial` declaration and reasoning (the table above) are unaffected.
