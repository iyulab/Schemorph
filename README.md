# Schemorph

> Declarative, SQL-first schema management built for the AI-agent era.

Schemorph is a CLI tool and library for managing database schemas as plain SQL files. You write the *desired state* of your database — tables, views, procedures — as ordinary `CREATE` statements. Schemorph computes what needs to change and applies it safely.

No ORM. No Visual Studio. No proprietary DSL. Just SQL, a single command, and output designed to be read by both humans and AI coding agents.

```bash
# Inspect a live database into SQL files
schemorph inspect --url "Server=...;Database=app" --out ./schema

# Preview what would change (never applies anything)
schemorph diff --url "..." --schema ./schema

# Apply the changes
schemorph apply --url "..." --schema ./schema
```

**Status: pre-alpha, under active development.** The core loop already runs against SQL Server — `inspect`, `diff` (with destructive-change gating and semantic exit codes), `apply` with a history ledger, and checksummed versioned migrations. Programmable-object strategy routing (`CREATE OR ALTER`) and packaging are still in progress; nothing is published yet. The documents in [`docs/`](./docs) define the project's anchors — see [Design Principles](./docs/design-principles.md) for what is fixed and what is open.

---

## Why Schemorph

Existing tools each solve part of the problem:

| | Declarative diff | Procedures / functions as first-class | Versioned data migrations | CLI-first, no login | AI-agent-native output |
|---|---|---|---|---|---|
| SSDT / SqlPackage | ✅ | ✅ | ⚠️ pre/post scripts | ⚠️ CLI exists, project system is VS-bound | ❌ |
| EF Migrations | ❌ (code-first) | ❌ | ⚠️ | ✅ | ❌ |
| Flyway / Liquibase | ⚠️ Flyway schema model, commercial editions only | ⚠️ repeatable scripts | ✅ | ✅ | ⚠️ Liquibase MCP / agent governance, paid tiers |
| Atlas | ✅ | ⚠️ limited on SQL Server | ✅ | ❌ SQL Server driver is paid (Pro) and requires login | ⚠️ |
| Bytebase | ⚠️ PostgreSQL only | ❌ | ✅ | ⚠️ self-hosted server, GUI-first | ⚠️ AI assistant; MCP is query-oriented |
| sqldef | ✅ | ⚠️ views/triggers only — no procedures/functions | ❌ | ✅ | ❌ |
| **Schemorph** | ✅ | ✅ | ✅ | ✅ | ✅ |

Schemorph's position: combine the **declarative diff model** (SSDT, Atlas, sqldef) with the **versioned migration ledger** (Flyway, Liquibase) in one coherent tool, and treat **AI coding agents as a primary user** rather than an afterthought.

On SQL Server specifically, that combination does not exist today as free, offline, login-free tooling: Atlas gates its SQL Server driver behind a paid plan, Bytebase's declarative workflow is PostgreSQL-only, Flyway's schema model requires commercial editions, and sqldef cannot manage procedures or functions. That gap is where Schemorph lives.

## Core Model

Schemorph treats different kinds of database objects with the strategy that actually fits them, instead of forcing everything through one mechanism:

```
schema/                      ← declarative: desired state, diffed automatically
  tables/
  views/
  procedures/                ← idempotent: CREATE OR ALTER, re-applied on change
  functions/
migrations/                  ← versioned: ordered, checksummed, run once
  V0001__seed_reference_data.sql
  V0002__backfill_legacy_codes.sql
```

- **Structural objects** (tables, indexes, constraints) are diffed: Schemorph compares your SQL files against the live database and generates the `CREATE` / `ALTER` / `DROP` plan.
- **Programmable objects** (procedures, functions, views, triggers) are re-applied idempotently via `CREATE OR ALTER`. Text-based diffing of procedure bodies is unreliable by nature; re-definition is not. This sidesteps an entire class of bugs that diff-only tools struggle with.
- **Data changes** (seeds, backfills, one-off transformations) are versioned migrations tracked in a checksummed history ledger, Flyway-style. State diffing cannot express these; a ledger can.

See [Architecture](./docs/architecture.md) for the full model.

## Built for AI Agents

Every command supports structured output and safe-by-default semantics:

- `--format json` on every command — plans, diffs, and errors as machine-parseable structures
- `diff` is always a dry run; `apply` requires explicit invocation, and destructive operations require an explicit flag
- Exit codes distinguish "no changes", "changes pending", and "error" so agents can branch on them
- An MCP server exposing the same operations as tools is a first-class deliverable, not a wrapper

The goal: an AI agent should be able to manage a schema change end-to-end — inspect, plan, review, apply — without screen-scraping human-oriented output.

## Database Support

- **SQL Server** — initial target. The diff engine builds on [DacFx](https://github.com/microsoft/DacFx) (Microsoft's schema comparison framework, the same engine behind SSDT), giving full-fidelity handling of T-SQL objects from day one.
- **PostgreSQL** — planned. The provider boundary is designed in from the start; see [ADR-0003](./docs/adr/0003-postgres-as-second-provider.md).

## Installation

> Not yet published. Planned distribution:

```bash
dotnet tool install -g Schemorph        # .NET tool
# plus standalone native binaries (win-x64, linux-x64, osx-arm64) per release
```

## Documentation

- [Design Principles](./docs/design-principles.md) — the project's anchors; read this first
- [Architecture](./docs/architecture.md) — the three-strategy model, ledger, provider boundary
- [Architecture Decision Records](./docs/adr/) — why the foundational choices were made

## Contributing

See [CONTRIBUTING.md](./CONTRIBUTING.md). The project follows ADR-driven governance: significant design decisions are proposed and recorded as ADRs before implementation.

## License

MIT. The core tool is and will remain free and fully functional offline — no accounts, no feature gates, no telemetry required for any local operation.
