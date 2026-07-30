# Limitations

What Schemorph does not do, and what to do instead. Everything here is measured —
each entry is pinned by a test, so if an engine upgrade changes the behavior we
find out rather than assume.

## Expressions that do not round-trip converge the database but not the plan

A declarative tool's core promise is convergence: apply the desired state, diff
again, and the plan is empty. Schemorph pins that invariant across the
expression-bearing shapes — column defaults (literal and function), CHECK
constraints, computed columns, filtered indexes, keys and references
(`ConvergenceTests`).

One shape breaks it. SQL Server does not store a CHECK constraint's text; it
stores a parsed expression and re-emits it in its own form. `IN (...)` comes back
as an OR chain:

```sql
-- what you wrote
CHECK (Status IN ('A', 'B', 'C'))
-- what the database returns
CHECK ([Status]='C' OR [Status]='B' OR [Status]='A')
```

DacFx compares the two expressions as text, so the desired state and the database
it just produced read as different — **forever**. The effects are all the ones
that matter:

- `diff` never empties; `status` always reports drift
- `apply` succeeds, changes nothing, and leaves the same plan behind
- `--expect-plan` fingerprints churn, and a CI plan-comment gate never goes quiet

The database itself is correct — the constraint is there and enforced. Only the
comparison disagrees.

**How to spot it:** the plan names the culprit. A change that will not converge
carries the slice of SQL the engine keeps re-issuing, so the churning constraint is
right there in `changes[].sql`:

```json
{ "objectName": "dbo.Orders", "actions": ["alter"],
  "sql": "ALTER TABLE [dbo].[Orders] DROP CONSTRAINT [CK_Orders_Status];" }
```

If the same object and the same constraint come back after a successful `apply`,
this is what you are looking at — not drift, and not a failed apply.

**What to do:** write the expression in the form the engine stores. The tool will
tell you what that is — `schemorph inspect` renders the live database as
desired-state SQL, so applying once and inspecting gives you the canonical text to
paste back:

```bash
schemorph inspect --url "$SCHEMORPH_URL" --out ./inspected
```

This is not specific to `IN`; any expression the engine normalizes differently
(redundant parentheses, `!=` vs `<>`, implicit schema qualification) can behave the
same way. `inspect` is the general remedy.

**Why it is not just fixed:** converging it would mean re-emitting arbitrary
expressions exactly the way SQL Server persists them — reimplementing the engine's
own normalizer, in the consumer of that engine. The cure is worse than the disease,
and a blunt alternative (ignoring check constraints in the comparison) would hide
real changes, which [Design Principles §4](design-principles.md) forbids.

## Column order is not compared

Schemorph diffs state, not ordinal position. A column's physical position is
neither declared in a model nor reachable by an `ALTER`: a column added to an
existing table lands last, whatever order the desired state lists it in, and a
generated file typically lists it ahead of trailing audit columns. Both providers
therefore exclude ordinal position from the comparison — SQL Server through
`IgnoreColumnOrder`, PostgreSQL by comparing columns by name.

The alternative is worse in a different way on each engine. On SQL Server,
honoring the difference makes the engine rebuild the whole table (new table, copy
rows, drop, rename) to re-seat a column, turning an additive change into full data
motion. On PostgreSQL there is no rebuild path at all, so the difference becomes a
change no statement can carry out: the plan never empties and the apply has nothing
to run.

**What this means for you:** if column position is material to your application —
a positional `INSERT`, a `SELECT *` whose column order is consumed — name the
columns explicitly. Schemorph will not reorder a table to match a file, and does
not report the difference as drift.

## Ordering across strategies is documented, not automatic

Schemorph runs the declarative publish, then re-definitions, then versioned
migrations ([ADR-0002](adr/0002-hybrid-object-strategy-model.md)). It does not
interleave them, so a migration that must run *between* two structural changes has
to be expressed as two applies. See
[ADR-0004 §6](adr/0004-failure-semantics-and-resume.md).

## Rollback restores shape, not data

There is no down-migration verb. Rolling back a structural change means putting the
SQL files back and applying them — the desired state *is* the rollback target
([ADR-0005](adr/0005-rollback-semantics.md)):

```bash
git revert <commit>            # or: git checkout <good-ref> -- schema/
schemorph diff  --url "$SCHEMORPH_URL" --schema ./schema   # read the plan first
schemorph apply --url "$SCHEMORPH_URL" --schema ./schema --expect-plan <planHash>
```

Two things this does not do. It **cannot bring back data**: a reverse structural diff
restores the shape, not the rows a destructive change removed — recovery from that is
a backup, not a plan. And it does not undo **versioned migrations**: those are events
in the ledger, run once and never un-run. To reverse a data migration, write a new
migration that does the reversing.

Reverting a structural change is usually destructive in the other direction (a column
that was added is now dropped), so the plan comes back gated — expect to pass
`--allow-destructive` once you have read it.

## Security principals are outside the declarative model

Users, logins, roles, role membership, and permissions are excluded from the
comparison ([ADR-0006](adr/0006-security-principals-out-of-declarative-model.md)):
a generated desired state never emits them, so treating their absence as "delete
them" would destroy live principals. Manage them through a separate operational
path.

## Two database engines, and one of them is partial

SQL Server is complete. PostgreSQL is released **up to a declared scope** — tables,
columns, constraints and the target schema ([ADR-0003](adr/0003-postgres-as-second-provider.md),
[ADR-0007](adr/0007-postgres-engine-selection.md)). Everything outside that
declaration — indexes, views, functions, procedures, triggers, versioned
migrations — is **refused with an error naming what the provider does support**,
never half-planned. The scope grows in releasable slices, with no committed
timeline; if you need the refused parts on PostgreSQL today, Atlas, sqldef, or
Flyway will serve you better.

The refusal is the contract, not a bug: a plan that cannot see a difference must
not claim a sync. Ask the provider what it covers with `schemorph schema` — the
capability list is part of the machine-readable manifest.

Parity means identical contract and equal capability range — deliberately **not**
identical limitations. The expression-comparison defect above is SQL-Server-specific:
the PostgreSQL comparison normalizes both sides through the engine's own renderings,
so enum-style CHECK constraints converge there.
