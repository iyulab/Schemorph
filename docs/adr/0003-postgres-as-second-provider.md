# ADR-0003: SQL Server first; PostgreSQL as a second provider behind a stable boundary

- **Status:** Accepted
- **Date:** 2026-07

## Context

The immediate need is SQL Server, where DacFx ([ADR-0001](./0001-csharp-dacfx-foundation.md)) provides an engine of unmatched fidelity — but DacFx is strictly SQL Server-only. PostgreSQL support is a known future requirement, not a current one.

The temptation in this situation is one of two errors:

1. **Design the abstraction around one concrete implementation** — the provider interface silently becomes "whatever DacFx does," making the second provider a rewrite of the core.
2. **Design the abstraction speculatively for N databases** — inventing a universal schema model before a second implementation exists to test it, producing complexity that the eventual Postgres work will contradict anyway.

## Decision

- Ship SQL Server first, alone.
- Define the provider boundary now, but keep it **minimal and behavioral**: inspect, compare, execute, dialect knowledge (see [Architecture](../architecture.md#the-provider-boundary)). The boundary specifies *what a provider does*, not *how schemas are modeled internally*.
- Everything user-facing — commands, file layout, plan format, ledger semantics, safety rules — is owned by the core and **must not leak provider specifics**. This is the concrete discipline that keeps the door open: if a design would make a CLI flag, plan field, or ledger column mean "the DacFx thing," it is wrong even while SQL Server is the only provider.
- The PostgreSQL provider's engine choice is **explicitly deferred** until that work begins. Candidates known today: wrapping `psqldef` as a subprocess, binding a Postgres parser library (e.g., the `pg_query` family), or native comparison against `information_schema`/`pg_catalog`. The right answer depends on the state of those projects at the time, and deciding now would be deciding with the least information.

## Consequences

**Positive**

- Full velocity on SQL Server now; no speculative abstraction tax.
- A written, testable definition of what "adding a database" means, before the second one arrives.
- The user-facing contract ("one mental model across databases," [Design Principles §7](../design-principles.md)) is protected by a review rule rather than by hope.

**Negative / accepted risks**

- The boundary will be wrong in some ways — every abstraction with one implementation is. The Postgres work will force revisions; this is planned for, and cheaper than either error described above.
- Until the second provider exists, "provider-independent" claims in the core are unverified. Mitigation: keep the core's schema-model surface small and expressed in Schemorph's own terms (plan actions, object classes, risk levels), never in DacFx types.
- Users needing PostgreSQL today are better served by existing tools (Atlas, sqldef, Flyway); the README should say so honestly rather than promise timelines.

## Addendum (2026-07): a committed consumer, and what it does *not* change

The decision above stands unchanged. What changed is the evidence around it.

A first consumer has committed to adopting the Postgres provider when it exists, and
supplied a **behavioral** requirement set with acceptance scenarios: managed,
non-superuser Postgres with no extension dependency in the core; the three-strategy
model at parity; expression-normalized diffs that re-run empty; plan, error, and MCP
contracts identical across providers; and transactional-DDL apply atomicity treated as
a Postgres-specific opportunity rather than an accident.

**What this changes:** the phase is no longer "deliberately unspecified" in its
*requirements*. There is now a written definition of what the provider must behave
like, and a partner to judge it.

**What it does not change:**

- **The engine choice stays deferred.** Behavioral requirements do not select an
  engine — they become the axes an engine is scored on. Requirements capture is not
  engine commitment, and conflating the two would be the same "decide with the least
  information" error this ADR was written to avoid.
- **The ordering stays.** Postgres remains last. The consumer holds a working
  alternative and no deadline; a committed consumer is a reason to specify the work,
  not to resequence the project.
- **The honesty stays.** One consumer is not a timeline. The README continues to point
  Postgres users at Atlas, sqldef, and Flyway, and promises no date.

**Counted honestly: N = 1.** This is recorded for prioritization, not as demand proof.
The provider was already an accepted phase, so the commitment adds a verification
partner rather than a justification.

### Evidence added to the engine-evaluation axes

Two of the axes are no longer hypothetical:

- **Control over expression normalization.** SQL Server exhibits this failure *today*:
  it stores `CHECK (… IN (…))` as an OR chain and DacFx compares expressions as text, so
  that shape never converges — the database is correct and the plan is not
  ([limitations](../limitations.md), pinned by `ConvergenceTests`). An engine that does
  not expose expression handling cannot fix this class; one wrapped as a subprocess
  cannot even see it.
- **Control over apply atomicity.** Postgres can put most DDL in a transaction, which
  SQL Server cannot. Whether that becomes a per-provider guarantee — and if so, that it
  must be *declared* in the plan rather than left implicit, since this ADR forbids
  provider specifics leaking into the user-facing contract — is an open question for
  kickoff, not for now.
  → **Resolved 2026-07-22** at [ADR-0007](./0007-postgres-engine-selection.md)'s acceptance:
  declared capability (`atomicity: transactional | partial`), carried into ADR-0004.

## Addendum (2026-07-22): the deferral is spent, and how demand is counted

The engine deferral this ADR created has been paid out as designed. The candidates were
scored at kickoff against the fixed axes, and the spike — not the feature lists — decided
it ([ADR-0007](./0007-postgres-engine-selection.md), Accepted). The deferral was worth
keeping: the defect that eliminated one candidate appears in no documentation, and only a
run against an acceptance-shaped schema exposed it.

Implementation proceeds in slices, each one releasable on its own, and **still without a
date**. What replaces a date is a declared capability surface: the provider states what it
can handle, refuses what it cannot rather than producing a plan it cannot stand behind, and
that surface grows monotonically toward the SQL Server one. Progress is therefore readable
from the tool itself instead of from a schedule.

**How demand is counted, since this ADR made honesty its discipline.** Two kinds of signal
exist and they are not interchangeable:

- **Committed** — a consumer that has supplied behavioral requirements and the acceptance
  scenarios it will judge adoption by. Still **N = 1**.
- **Directional** — new projects expected to choose Postgres over SQL Server. Real, and
  recorded, but it is a forecast. Promotion to committed requires the same thing the first
  consumer supplied: requirements and acceptance criteria.

Directional signal is a legitimate reason to **start**, and it is why the work is underway.
It is not a reason to promise a date, and this ADR's stance is unchanged on that point:
the README continues to point Postgres users at other tools until a slice actually ships,
and then says what that slice covers — not when the next one arrives.
