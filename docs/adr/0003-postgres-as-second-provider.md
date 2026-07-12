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
