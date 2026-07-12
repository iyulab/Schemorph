# Roadmap

Phases, not dates. Each phase should produce something usable end-to-end before the next begins. Scope within phases is directional; items move as implementation teaches us.

## Phase 0 — Spike (de-risk the foundation)

Validate the load-bearing assumptions before committing to structure:

- [ ] DacFx `SchemaComparison` driven headlessly: file-based desired state vs. live database, structured result extraction
- [ ] Native AOT (or trimmed self-contained) build with DacFx in the dependency graph — confirm single-file distribution is achievable ([ADR-0001](./adr/0001-csharp-dacfx-foundation.md) flags this as unverified)
- [ ] `CREATE OR ALTER` re-definition flow including dependency ordering on a realistic procedure set

Exit criterion: confidence that the three strategies are implementable on the chosen stack, or an ADR revising the stack.

## Phase 1 — MVP: SQL Server, CLI, the full loop

The smallest thing that is honestly useful:

- [ ] `inspect` — live database → desired-state SQL files in the conventional layout
- [ ] `diff` — desired state vs. live database → plan (human text + `--format json`)
- [ ] `apply` — execute a plan; history ledger created and written
- [ ] Versioned migrations (`migrations/V*.sql`) with checksum verification
- [ ] Idempotent programmable-object application with per-file checksums
- [ ] Destructive-change gating (off by default, explicit flag, marked in plan)
- [ ] Semantic exit codes
- [ ] CI-friendly: everything runnable non-interactively

Exit criterion: a real project (dogfooding target: an internal SQL Server product schema) manages its schema exclusively through Schemorph.

## Phase 2 — Agent-native surface

- [ ] Stabilize and version the JSON plan schema
- [ ] MCP server exposing inspect / diff / apply / status as tools
- [ ] Plan explanations ("this change forces a table rebuild because...")
- [ ] Safety lint rules operating on the plan (destructive drops, non-nullable additions without defaults, type changes with rebuild cost)
- [ ] GitHub Actions recipe: post the plan as a PR comment

Exit criterion: an AI coding agent completes a schema change end-to-end (edit SQL file → review plan → apply to dev database) without human-oriented output parsing.

## Phase 3 — Hardening & ecosystem

- [ ] Drift detection (`status`: has the database diverged from the last applied state?)
- [ ] Baseline/onboarding flow for brownfield databases with existing migration history from other tools
- [ ] Multi-environment configuration (dev/staging/prod targets, per-target safety policy)
- [ ] Documentation site, examples, comparison guides

## Phase 4 — PostgreSQL provider

Deliberately last and deliberately unspecified ([ADR-0003](./adr/0003-postgres-as-second-provider.md)):

- [ ] Evaluate engine options as they exist *then* (psqldef subprocess, parser-library binding, native catalog comparison)
- [ ] ADR recording the choice
- [ ] Implement behind the provider boundary; revise the boundary where reality disagrees with it
- [ ] The test that matters: identical commands, file layout, plan format, and ledger semantics on both databases

## Explicitly not planned

- GUI (structured output exists so others can build one)
- Hosted service (would be additive if ever pursued; see [Design Principles §5](./design-principles.md))
- MySQL/SQLite/other providers — plausible eventually, not before the two-provider boundary has proven itself
