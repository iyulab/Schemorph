# ADR-0007: PostgreSQL provider engine — native catalog comparison with shadow normalization

- **Status:** Proposed — acceptance is a human decision, gated together with the
  open apply-atomicity question from [ADR-0003](./0003-postgres-as-second-provider.md)'s addendum
- **Date:** 2026-07 (candidate survey and spikes run 2026-07-21)

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
