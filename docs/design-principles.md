# Design Principles

These are the anchors of Schemorph. They are deliberately few, and deliberately stable: implementation details may change freely, but a change to this document is a change to what the project *is* and requires an ADR.

Everything not fixed here is an open implementation decision, to be settled when the code forces the question — not before.

---

## 1. SQL is the source of truth

The schema lives in plain `.sql` files containing standard DDL. Not C# classes, not HCL, not YAML, not a DSL.

Consequences:

- Anyone who knows SQL can read, write, and review the entire schema with no tool-specific knowledge.
- `git diff` on schema files is meaningful on its own.
- The schema is independent of any application. Multiple services, and multiple languages, can share one schema repository.
- Schemorph never requires generating SQL *from* something else. Generating something else (docs, types, diagrams) *from* SQL is fair game.

Anti-goal: Schemorph will not become an ORM, a modeling language, or an abstraction over SQL.

## 2. Fit the strategy to the object, not the object to the strategy

Single-mechanism tools fail predictably: pure diff tools mangle procedure bodies; pure migration tools bury the current schema under years of deltas. Schemorph commits to a hybrid:

| Object class | Strategy | Why |
|---|---|---|
| Tables, columns, indexes, constraints | **Declarative diff** | Structural state is well-defined; diffing is reliable and eliminates hand-written ALTERs |
| Procedures, functions, views, triggers | **Idempotent re-definition** (`CREATE OR ALTER`) | Bodies are opaque text; re-applying the whole definition is trivially correct where body-diffing is fragile |
| Data changes (seeds, backfills, one-offs) | **Versioned, checksummed migrations** | Data changes are events, not states; only a ledger can represent them |

This three-way split is an anchor. The exact classification of edge-case object types (sequences, user-defined types, permissions, ...) is an open decision to be made per-provider as implementation reveals their behavior. **Security principals** (users, logins, roles, role membership, permissions) are the first such edge case settled: on SQL Server they are excluded from the declarative diff — a code-generated desired-state never emits them and dropping a login for being "absent" is the silent destruction §4 forbids ([ADR-0006](./adr/0006-security-principals-out-of-declarative-model.md)).

## 3. Every interface is programmable

The same information is available to a person reading a terminal and to a program parsing a stream — and the structured form is the authoritative one:

- **Structured output everywhere.** Every command that produces information supports `--format json` with a stable, versioned shape. Human-readable output is a rendering of the structured form, never the other way around.
- **Plans before actions.** Any operation that would modify a database can produce its full plan without executing it.
- **Semantics in exit codes.** "Nothing to do", "changes pending", and "failure" are distinguishable without parsing text.
- **Explainability.** When Schemorph decides something non-obvious (e.g., "this column change requires a table rebuild"), the plan says so and says why.
- **MCP surface.** The same operations are exposed as an MCP server, so a host that speaks MCP calls them as tools instead of shelling out and scraping text.

This principle constrains API design continuously; it is not a feature to be added later. It also runs the other way: a machine-readable surface is never a reason to ship output a person cannot review. The same plan renders as a review document (`diff --format sql`) whose fingerprint is what a gated `apply` will execute — the reviewer and the program are looking at one artifact, not two. ([ADR-0008](./adr/0008-interface-anchor-names-a-property.md))

## 4. Safe by default, honest about danger

- Reading and planning are always safe. `diff` never modifies anything.
- Destructive operations (anything that loses data that cannot be recomputed) are excluded from plans unless explicitly enabled, and are prominently marked when enabled. The criterion is the loss, not the syntax: a `DROP` of an object that holds data qualifies, and so does an `ALTER` that removes a column the desired state no longer declares — a plan is built per object, so the second arrives wearing the first's clothes. A column that is *re-created* does not qualify: its new values are the new definition's output, so they are replaced rather than lost, and the lint band describes it instead. Where a provider cannot prove the distinction it reports nothing and the coarser object-level classification stands — under-claiming is always the safe direction for a dialect judgment.
- Every `apply` records what ran, when, its checksum, and its outcome in the history ledger. The ledger is the audit trail.
- Schemorph does not pretend to make schema changes risk-free. It makes risk *visible* — the rest is judgment, which belongs to the user and to review processes, not to the tool.

## 5. Local-first, no strings attached

Every capability of the core tool works offline, forever, without an account, license key, or network call. This is a hard commitment, not a pricing tier that happens to be free today.

If a hosted or server-dependent layer ever exists (shared plan registries, drift monitoring, team workflows), it is additive and lives outside the core. The core never degrades to promote it.

## 6. Leverage engines, own the experience

Schemorph does not rewrite what already works:

- The SQL Server diff engine is **DacFx** — Microsoft's own schema-comparison framework, battle-tested across the full breadth of T-SQL. Reimplementing it would be years of work to reach lower fidelity. ([ADR-0001](./adr/0001-csharp-dacfx-foundation.md))
- Future providers may likewise wrap existing engines where quality ones exist.

What Schemorph owns — and where all of its value concentrates — is everything around the engine: the object-strategy model, the ledger, the plan format, the CLI ergonomics, the safety semantics, the machine-readable interface. Engines are replaceable; the experience is the product.

## 7. One mental model across databases

Schemorph is multi-target by design. SQL Server came first; PostgreSQL is released up to a declared capability scope; further engines are welcome behind the same boundary. The provider boundary exists so that commands, file layout, plan format, ledger semantics, and safety rules are identical across databases — only the dialect underneath changes. ([ADR-0003](./adr/0003-postgres-as-second-provider.md))

Anyone who learns Schemorph on one database has learned it for all of them.

---

## What this document is not

It is not a specification. Command names, flag spellings, file formats, the ledger's exact schema, configuration syntax, plugin mechanisms — all open. When an implementation question arises, the test is: *which answer better serves the seven anchors above?* If the anchors don't decide it, it's a genuine free choice; make it, record it if consequential, move on.
