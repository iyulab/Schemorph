# Architecture

This document describes the shape of Schemorph at the level that should remain true across implementation iterations. Component internals, exact schemas, and API signatures are intentionally not specified here.

## Overview

```
                ┌─────────────────────────────────────────────┐
                │                 Interfaces                   │
                │   CLI (schemorph)      MCP server            │
                └────────────────┬────────────────────────────┘
                                 │
                ┌────────────────▼────────────────────────────┐
                │             Orchestration Core               │
                │  · object-strategy routing                   │
                │  · plan model (build / render / explain)     │
                │  · history ledger                            │
                │  · safety policy (destructive gating, lint)  │
                └────────┬───────────────────────┬─────────────┘
                         │   provider boundary   │
                ┌────────▼─────────┐   ┌─────────▼─────────┐
                │  SQL Server      │   │  PostgreSQL       │
                │  provider        │   │  provider         │
                │  (DacFx-based)   │   │  (planned)        │
                └──────────────────┘   └───────────────────┘
```

Two things carry the project's identity: the **orchestration core** (strategy model, plan, ledger, safety) and the **interfaces** (CLI, MCP). Providers are adapters; they are expected to be replaceable.

## The Three Strategies

### 1. Declarative diff — structural objects

Tables, columns, indexes, and constraints are defined as `CREATE` statements in `schema/`. On `diff`/`apply`, the provider compares the desired state against the live database and produces a change plan (`CREATE` / `ALTER` / `DROP`).

For SQL Server this comparison is delegated to DacFx, which handles the hard parts — dependency ordering, data-preserving table rebuilds, the long tail of T-SQL object options — with the same fidelity as SSDT.

The core consumes the provider's raw comparison and turns it into a Schemorph plan: classified, annotated with risk, and renderable as JSON or human text.

### 2. Idempotent re-definition — programmable objects

Procedures, functions, views, and triggers are stored one object per file and applied via `CREATE OR ALTER` (SQL Server 2016+) / `CREATE OR REPLACE` (PostgreSQL). Schemorph tracks a checksum per object file; on `apply`, changed objects are re-applied in dependency order.

Rationale: procedure bodies are semantically opaque text. Comparing them structurally is unreliable in every tool that attempts it. Re-definition is idempotent, deterministic, and uses a mechanism the database itself guarantees. The "diff" for these objects is simply *changed / unchanged / new / removed* at file granularity — which is also exactly what a code reviewer or AI agent wants to see, alongside the git diff of the body itself.

### 3. Versioned migrations — data changes

Ordered files (`V0001__description.sql`) applied exactly once, tracked in the history ledger with checksums. Modifying an already-applied migration is detected and rejected. This is the Flyway model, adopted deliberately and without shame: for data changes it is correct, and nothing declarative can replace it.

## The History Ledger

A table Schemorph maintains in the target database, recording every applied change across all three strategies: what ran, its checksum, when, by whom/what, and the outcome.

Anchor-level commitments:

- The ledger is the single audit trail for all Schemorph activity against a database.
- Checksums make tampering with already-applied migrations detectable.
- The ledger records *idempotent re-definitions* and *declarative applies* as well — not only versioned migrations — so the full change history of the database is reconstructible from one place.

Exact table name, columns, and retention semantics: implementation decisions.

## The Plan

Every mutating operation is expressible as a plan before it is executed. The plan is the central data structure of Schemorph and the primary contract with AI agents:

- Each planned action carries: the object, the operation, the generated SQL, a risk classification (safe / warning / destructive), and — where non-obvious — an explanation.
- Plans are renderable as human text and as versioned JSON.
- `diff` produces a plan and stops. `apply` produces a plan and executes it. The same plan format serves both.

Safety linting (detecting destructive drops, non-nullable additions without defaults, type changes forcing rebuilds, etc.) operates on the plan, provider-independently where possible.

## The Provider Boundary

A provider supplies, at minimum:

1. **Inspect** — read a live database into desired-state SQL files
2. **Load** — read and classify a desired-state directory once per operation (model files vs. deploy scripts/seed DML); the resulting handle feeds compare, apply, and programmable analysis so a single operation never re-reads or re-parses the directory
3. **Compare** — desired state vs. live database → raw structural changes (strategy 1)
4. **Execute** — run generated SQL and object re-definitions transactionally where the database allows
5. **Dialect knowledge** — object classification, `CREATE OR ALTER` equivalents, quoting, batch separators

The core owns everything else. A provider should never need to know about the ledger format, the plan format, the CLI, or the MCP surface.

The SQL Server provider wraps DacFx in-process (see [ADR-0001](./adr/0001-csharp-dacfx-foundation.md)). The PostgreSQL provider's engine choice — wrap an existing tool, bind a parser library, or build native comparison — is explicitly deferred until that work begins (see [ADR-0003](./adr/0003-postgres-as-second-provider.md)).

## Interfaces

### CLI

A `dotnet tool` plus standalone native binaries. Verb-oriented (`inspect`, `diff`, `apply`, `status`, ...), with `--format json` universally available. Exit codes are semantic and failures carry a typed error envelope ([errors.md](./errors.md)). Exact verb set and flags evolve with usage.

### MCP Server

Exposes the same operations as MCP tools so AI agents can plan and apply schema changes as first-class tool calls, and the current schema state / plan as MCP resources (`schemorph://schema`, `schemorph://schema/{kind}/{name}`, `schemorph://plan`) so hosts can attach them as context. The MCP surface and the CLI are two renderings of the same core API — neither is a wrapper around the other's text output.

## Repository Layout (user's project)

```
schema/
  tables/          one file per table (or grouped; layout is convention, not enforcement)
  views/
  procedures/
  functions/
migrations/
  V0001__*.sql
```

The layout above is the default convention; paths are passed as arguments
(`--schema`, `--migrations`) and the tree is not hard-coded. A project
configuration file (target, paths, safety policy) is a *designed-for*, not a
shipped, surface: introducing it is demand-driven and tied to the multi-environment
work ([ADR-0006](adr/0006-security-principals-out-of-declarative-model.md)).
Nothing today reads one.

What *is* enforced is content, not layout: the schema directory is classified file-by-file, and only declarative DDL enters the model. Deploy scripts living alongside model files (SQLCMD `:r`/`:setvar`/`$(var)`, EXEC-only touch-ups, seed DML) are skipped with a per-file warning instead of poisoning the comparison — so an existing SSDT project tree can be pointed at as-is. A file that fails to parse *without* SQLCMD evidence is a hard, file-attributed error, never a silent skip (a false skip would surface as a phantom DROP).

## Non-Goals

- Not an ORM, query builder, or application framework.
- Not a GUI (though structured output should make building one trivial for anyone who wants to).
- Not a data-sync or replication tool; the ledger tracks schema/data *changes*, not data itself.
- Not a multi-database abstraction that hides dialect differences in the SQL you write — your SQL is your database's SQL.
