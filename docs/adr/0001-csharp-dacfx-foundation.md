# ADR-0001: C# with DacFx as the SQL Server diff engine

- **Status:** Accepted
- **Date:** 2026-07

## Context

Schemorph needs a schema-comparison engine for its initial target, SQL Server. The realistic options:

1. **Build a T-SQL parser and diff engine from scratch.** Full control, but T-SQL's surface area (partitioning, CLR objects, security policies, temporal tables, full-text, ...) took Microsoft years to cover in SSDT. Reaching comparable fidelity is a multi-year effort that produces a worse engine.
2. **Wrap an existing cross-database tool** (e.g., sqldef's `mssqldef`). Single binary, MIT-licensed, but its coverage of programmable objects and advanced T-SQL is materially weaker than DacFx, and it is an out-of-process Go binary regardless of our implementation language.
3. **Use DacFx** (`Microsoft.SqlServer.DacFx`) — the .NET library underlying SSDT and SqlPackage. MIT-adjacent licensing across its components (ScriptDom is MIT), cross-platform (.NET Standard 2.0), actively maintained by Microsoft, and exposing schema comparison (`SchemaComparison`), extraction, and deployment as an in-process API.

Option 3 is only ergonomic from .NET: using DacFx from Rust/Go would require hosting a separate .NET process and IPC, negating most of its benefit.

A secondary consideration: Schemorph's initial user base (SQL Server shops) overwhelmingly lives in the .NET ecosystem, and the maintaining organization's existing library portfolio is predominantly .NET.

## Decision

- Implement Schemorph in **C# / .NET**.
- Build the SQL Server provider on **DacFx in-process** for structural comparison, extraction, and deployment-script generation.
- Distribute as a `dotnet tool` **and** as standalone binaries via **Native AOT** (or self-contained trimmed builds where AOT is blocked by dependencies), so that non-.NET users and CI environments get a single-file executable with fast startup.

## Consequences

**Positive**

- Day-one fidelity on the hardest part of the problem (T-SQL breadth), inherited from a Microsoft-maintained engine.
- Full in-process control over comparison options, result inspection, and script generation — no text-parsing of another tool's output.
- Natural reuse of the maintainer's existing .NET infrastructure and contributor pool.

**Negative / accepted risks**

- **DacFx is SQL Server-only.** The PostgreSQL provider cannot reuse it; this is accepted and addressed by the provider boundary ([ADR-0003](./0003-postgres-as-second-provider.md)).
- **AOT compatibility of DacFx — verified 2026-07-12 (Phase 0 spike, DacFx 170.4.83 / .NET 10): Native AOT is not viable.** ILC compiles DacFx with warnings only, but the binary crashes at runtime — DacFx's extensibility layer loads `DatabaseSchemaProvider` types reflectively by type-name string, which trimming removes (`ExtensibilityException: Sql150DatabaseSchemaProvider is not valid`). Rooting the DacFx assemblies (`TrimmerRootAssembly`) does not help either: the build takes ~35 minutes, produces a 145 MB binary, and still crashes — DacFx's `ModelStore` instantiates generic types at runtime via `Activator` (`ColumnData`\`8`), which AOT cannot pre-generate ("missing native code or metadata"). The incompatibility is structural, not configurational. `PublishTrimmed` fails for the same reflection reasons. **The confirmed fallback is self-contained un-trimmed single-file with `IncludeAllContentForSelfExtract=true`** — required because DacFx's dacpac writer calls `Assembly.Location`, which is empty in single-file apps without self-extract (IL3000). Measured: single 171 MB executable, works end-to-end, startup overhead negligible relative to comparison cost. Single-file distribution is therefore achievable; binary size vs. distribution strategy (compression, per-platform assets) is a Phase 1 packaging decision.
- We track DacFx's release cadence for new SQL Server features — the same burden any approach would carry, but here it is Microsoft's code that must be updated, not ours.
- C# has less mindshare than Rust/Go in the current wave of OSS infrastructure tooling. Mitigation: distribution as native binaries makes the implementation language invisible to users.

## Alternatives considered

- **Rust + sqldef subprocess for all databases:** uniform architecture, strong OSS branding, but sacrifices exactly the fidelity (procedures, triggers, security objects) that motivates the project, and adds a second runtime's toolchain for no functional gain on SQL Server.
- **Thin scripting around SqlPackage CLI:** avoids library integration but reduces Schemorph to output-parsing another CLI, with no access to structured comparison results — incompatible with the plan-centric, agent-first design.
