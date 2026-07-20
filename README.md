# Schemorph

[![NuGet](https://img.shields.io/nuget/v/Schemorph?logo=nuget)](https://www.nuget.org/packages/Schemorph)
[![CI](https://github.com/iyulab/Schemorph/actions/workflows/ci.yml/badge.svg)](https://github.com/iyulab/Schemorph/actions/workflows/ci.yml)
[![Release](https://github.com/iyulab/Schemorph/actions/workflows/release.yml/badge.svg)](https://github.com/iyulab/Schemorph/releases)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet)](https://dotnet.microsoft.com/)
[![License: MIT](https://img.shields.io/badge/license-MIT-green)](./LICENSE)

> Declarative, SQL-first schema management built for the AI-agent era.

Schemorph is a CLI tool and library for managing database schemas as plain SQL files. You write the *desired state* of your database — tables, views, procedures — as ordinary `CREATE` statements. Schemorph computes what needs to change and applies it safely.

No ORM. No Visual Studio. No proprietary DSL. Just SQL, a single command, and output designed to be read by both humans and AI coding agents.

```bash
# Inspect a live database into SQL files
schemorph inspect --url "Server=...;Database=app" --out ./schema

# Preview what would change (never applies anything) — prints the plan and its fingerprint
schemorph diff --url "..." --schema ./schema

# Apply exactly the reviewed plan, or refuse if anything changed since the diff
schemorph apply --url "..." --schema ./schema --expect-plan <hash>

# Drift, ledger summary, pending migrations
schemorph status --url "..." --schema ./schema --migrations ./migrations

# Run as an MCP server (read-only tools + gated apply) for AI agents
schemorph mcp
```

**Status: early (0.x), under active development.** Released on [NuGet](https://www.nuget.org/packages/Schemorph) and [GitHub releases](https://github.com/iyulab/Schemorph/releases). Working today against SQL Server: the full loop (`inspect` / `diff` / `apply` / `status`) with destructive-change gating and semantic exit codes; programmable-object routing via `CREATE OR ALTER`; checksummed run-once migrations; a history ledger; brownfield adoption (existing databases and SSDT trees are consumed as-is — matching objects reconcile instead of re-applying); a versioned machine-readable [plan format](./docs/plan-format.md) with per-change SQL and explanations, an apply gate (`--expect-plan`) and [safety-lint warnings](./docs/errors.md); and an MCP server (`schemorph mcp`) with schema/plan resources. The documents in [`docs/`](./docs) define the project's anchors — see [Design Principles](./docs/design-principles.md) for what is fixed and what is open.

---

## Why Schemorph

On SQL Server, one combination of properties does not exist today as free, offline, login-free tooling: a **declarative diff engine** that treats **procedures and functions as first-class**, plus a **versioned data-migration ledger**, plus **output an AI agent can act on safely**. Every existing tool gives you some of these; none gives you all of them:

| | Declarative diff | Procedures / functions as first-class | Versioned data migrations | CLI-first, no login | AI-agent-native output |
|---|---|---|---|---|---|
| SSDT / SqlPackage | ✅ | ✅ | ⚠️ pre/post scripts | ⚠️ CLI exists, project system is VS-bound | ❌ |
| EF Migrations | ❌ (code-first) | ❌ | ⚠️ | ✅ | ❌ |
| Flyway / Liquibase | ⚠️ Flyway schema model, commercial editions only | ⚠️ repeatable scripts | ✅ | ✅ | ⚠️ Liquibase MCP / agent governance, paid tiers |
| Atlas | ✅ | ⚠️ limited on SQL Server | ✅ | ❌ SQL Server driver is paid (Pro) and requires login | ⚠️ |
| Bytebase | ⚠️ PostgreSQL only | ❌ | ✅ | ⚠️ self-hosted server, GUI-first | ⚠️ AI assistant; MCP is query-oriented |
| sqldef | ✅ | ⚠️ views/triggers only — no procedures/functions | ❌ | ✅ | ❌ |
| **Schemorph** | ✅ | ✅ | ✅ | ✅ | ✅ |

<sup>Competitive claims as of 2026-07; re-verified periodically as these tools evolve.</sup>

- Atlas gates its SQL Server driver behind a paid plan; Bytebase's declarative workflow is PostgreSQL-only; Flyway's schema model requires commercial editions; sqldef cannot manage procedures or functions; SSDT is bound to the Visual Studio project system.
- "AI-agent-native" here is not a roadmap bullet: plans are a versioned machine contract ([plan-format.md](./docs/plan-format.md)), `apply` executes only a reviewed plan fingerprint or refuses, and the same operations run as gated MCP tools — verified by an end-to-end agent harness on every integration run.

Schemorph is that combination: the **declarative diff model** (SSDT, Atlas, sqldef) and the **versioned migration ledger** (Flyway, Liquibase) in one coherent tool, with **AI coding agents as a primary user** rather than an afterthought.

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

- **Versioned plan format** ([docs/plan-format.md](./docs/plan-format.md)) — plans as machine contracts, with `--format json` on every command; JSON is the default whenever stdout is redirected (agents pipe, humans get text)
- **The apply gate** — `diff` prints the plan's fingerprint (`planHash`); `apply --expect-plan <hash>` executes exactly the reviewed plan or refuses (`plan_mismatch`) if anything drifted in between
- `diff` is always a dry run; destructive operations require an explicit flag
- Exit codes distinguish "no changes", "changes pending", and "error", and failures carry a typed `{kind, code, message, hint}` envelope — see [Errors and exit codes](./docs/errors.md)
- `schemorph schema` prints a JSON manifest of the whole CLI surface (verbs, options, exit codes) so agents discover it without parsing help text
- Credentials come from the `SCHEMORPH_URL` environment variable (preferred over `--url`), and password material is redacted from every output channel — safe to pipe into logs and PR comments (there is a [ready-made GitHub Actions recipe](./docs/recipes/github-actions-plan-comment.md) that posts the plan on every schema PR)
- **`schemorph mcp`** runs the same operations as an MCP server over stdio — read-only tools (`schemorph_diff`, `schemorph_inspect`, `schemorph_status`) plus `schemorph_apply` gated behind a required plan fingerprint; the connection string stays in the server's environment, never in the conversation
- **Schema as context** — the MCP server also exposes *resources*: `schemorph://schema` (the live database as desired-state SQL, or one object via `schemorph://schema/{kind}/{name}`) and `schemorph://plan` (the current plan with its `planHash`, ready for the apply gate; set `SCHEMORPH_SCHEMA_DIR` in the server environment), so hosts attach schema state directly instead of round-tripping tool calls
- **Agent Skill** — [`skills/schemorph/SKILL.md`](./skills/schemorph/SKILL.md) packages the procedural knowledge (the edit → diff → review → gated apply → converge loop, error-contract branching, migration immutability) in the [Agent Skills](https://agentskills.io) format; drop it into your agent's skills directory

An AI agent manages a schema change end-to-end — inspect, plan, review, apply — without screen-scraping human-oriented output.

## Database Support

- **SQL Server** — initial target. The diff engine builds on [DacFx](https://github.com/microsoft/DacFx) (Microsoft's schema comparison framework, the same engine behind SSDT), giving full-fidelity handling of T-SQL objects from day one.
- **PostgreSQL** — planned, with no committed timeline. The provider boundary is designed in from the start and a first consumer has supplied its requirements, but the engine choice is still deliberately open; see [ADR-0003](./docs/adr/0003-postgres-as-second-provider.md). **If you need PostgreSQL today, Atlas, sqldef, or Flyway will serve you better** — that is a more useful answer than a date we cannot keep.

## Installation

```bash
dotnet tool install -g Schemorph
```

Standalone self-contained binaries (win-x64, linux-x64, osx-arm64) are attached to each
[GitHub release](https://github.com/iyulab/Schemorph/releases) — no .NET runtime required.

## Documentation

- [Design Principles](./docs/design-principles.md) — the project's anchors; read this first
- [Architecture](./docs/architecture.md) — the three-strategy model, ledger, provider boundary
- [The plan format](./docs/plan-format.md) — the machine-readable plan contract and its versioning
- [Errors and exit codes](./docs/errors.md) — the typed error envelope agents branch on, and the safety-lint warning band
- [When an apply fails](./docs/failure-semantics.md) — what the database looks like after a partial apply, how to recover, and how to read the ledger
- [Limitations](./docs/limitations.md) — what does not converge or is out of scope, and what to do instead
- [Recipes](./docs/recipes/) — ready-made integrations (GitHub Actions plan-on-PR comment)
- [Architecture Decision Records](./docs/adr/) — why the foundational choices were made

## Contributing

See [CONTRIBUTING.md](./CONTRIBUTING.md). The project follows ADR-driven governance: significant design decisions are proposed and recorded as ADRs before implementation.

## License

MIT. The core tool is and will remain free and fully functional offline — no accounts, no feature gates, no telemetry required for any local operation.
