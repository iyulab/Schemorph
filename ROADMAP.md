# Roadmap

Phases, not dates. Each phase should produce something usable end-to-end before the next begins. Scope within phases is directional; items move as implementation teaches us.

> Canonical roadmap. Session handoff and working logs live in `claudedocs/` (untracked, maintainer-local).

## Phase 1 — MVP: SQL Server, CLI, the full loop (in progress)

The core loop — `inspect` / `diff` / `apply` (+ history ledger) / versioned migrations — is implemented and verified end-to-end against LocalDB (see History). Remaining:

- [ ] **Idempotent programmable-object application with per-file checksums** — complete the strategy routing: exclude programmable objects (procedures/functions/views/triggers) from the DacFx declarative path (`ExcludeObjectTypes`) and route them through per-file checksums + `CREATE OR ALTER` + dependency ordering. Port source: `spikes/create-or-alter` (validated). Completes the ADR-0002 hybrid model.
- [ ] **Failure-semantics ADR** — partial failure, resume, ledger crash-consistency: what state the ledger and database are in when apply fails mid-stream (connection loss, Nth statement failure, non-transactional DDL), and how a run resumes (re-run vs checkpoint). Precondition for the ledger's "single audit trail" promise.
- [ ] **Rollback / down-migration ADR** — decide and document rollback semantics per strategy (declarative: reverse diff; idempotent: prior definition; versioned data: explicit down or unsupported). "Not supported" is an acceptable outcome, but it must be an explicit, reasoned decision.
- [ ] **Credential handling policy** — environment-variable / credential-source support preferred over `--url`, plus secret redaction across every output channel (logs, plan text/JSON, error output). Agent-first positioning means outputs get re-propagated mechanically, making redaction load-bearing.
- [ ] **Typed error envelope + exit-code enum** — stable machine-readable error contract (`{kind, code, message, hint}`) and a documented exit-code↔error-kind mapping. This is an early contract; changing it later is breaking.
- [ ] **CI + regression-test infrastructure** — build/unit-test CI, real-database integration tests (testcontainers), and a golden-corpus diff regression suite (promote Phase 0 spike scenarios into fixtures; guards against DacFx `SchemaComparison` edge-case behavior — the most plausible failure mode for this tool).
- [ ] **DacFx version policy** — version floor, pinned dependency, upgrades promoted only after golden-corpus verification; track the quarterly release cadence. Folded into the test-infrastructure work.
- [ ] **Release engineering** (late Phase 1) — tag-triggered release workflow: NuGet (`dotnet tool install -g Schemorph`), win-x64 / linux-x64 / osx-arm64 self-contained binaries.

Exit criterion: a real project (dogfooding target: an internal SQL Server product schema) manages its schema exclusively through Schemorph.

## Phase 2 — Agent-native surface

- [ ] Stabilize and version the JSON plan schema, aligned with Terraform/OpenTofu machine-readable-plan conventions (`format_version`, per-change object + action list, JSONL streaming for long-running commands)
- [ ] Agent-first CLI conventions: JSON default on non-TTY stdout, self-describing `schema` subcommand (subcommands / flags / output formats / exit codes as a JSON manifest), `next_actions` hints in responses
- [ ] MCP server exposing inspect / diff / apply / status as tools — safety model: read-only/plan-only by default, explicit apply gate, single-statement enforcement
- [ ] Schema-as-context: expose current schema state and plans as MCP *resources*, not just tools
- [ ] Plan explanations ("this change forces a table rebuild because...") — per-action SQL/Explanation linkage
- [ ] Safety lint rules operating on the plan (destructive drops, non-nullable additions without defaults, type changes with rebuild cost) — extended to static checks on versioned migration files (unguarded mass UPDATE/DELETE, TRUNCATE, GRANT), depth vs false-positive trade-off to be decided
- [ ] Agent-usability verification harness — a reproducible end-to-end scenario (edit SQL → diff → review plan → apply) driven by an actual coding agent, making the exit criterion below measurable
- [ ] Agent Skill packaging (`SKILL.md`) alongside MCP (late Phase 2, once CLI/MCP surfaces stabilize)
- [ ] GitHub Actions recipe: post the plan as a PR comment

Exit criterion: an AI coding agent completes a schema change end-to-end (edit SQL file → review plan → apply to dev database) without human-oriented output parsing.

## Phase 3 — Hardening & ecosystem

- [ ] Drift detection (`status`: has the database diverged from the last applied state?)
- [ ] Baseline/onboarding flow for brownfield databases with existing migration history from other tools
- [ ] Multi-environment configuration (dev/staging/prod targets, per-target safety policy) — design should leave the door open to policy-as-code (machine-evaluable policies failing CI on violating plans) without pre-building it
- [ ] Documentation site, examples, comparison guides — positioning around the empirically open combination: SQL Server declarative diff + programmable objects first-class + versioned data ledger + free/no-login/offline (final wording is a human decision)

## Phase 4 — PostgreSQL provider

Deliberately last and deliberately unspecified ([ADR-0003](docs/adr/0003-postgres-as-second-provider.md)):

- [ ] Evaluate engine options as they exist *then* (psqldef subprocess, parser-library binding, native catalog comparison)
- [ ] ADR recording the choice
- [ ] Implement behind the provider boundary; revise the boundary where reality disagrees with it
- [ ] The test that matters: identical commands, file layout, plan format, and ledger semantics on both databases

## Watching (deliberately not planned)

- **Declarative seed/reference data** (cf. Atlas v1.1 "Declarative Data Management") — in direct tension with ADR-0002's anchor ("data changes are events, not state"); adopting it would require an anchor-revising ADR. Revisit only on real user demand.

## Explicitly not planned

- GUI (structured output exists so others can build one)
- Hosted service (would be additive if ever pursued; see [Design Principles §5](docs/design-principles.md))
- MySQL/SQLite/other providers — plausible eventually, not before the two-provider boundary has proven itself

---

## History

- **2026-07-12** — Backlog-discovery proposal (17 items) reviewed: Phase 1/2 scope above reflects the adopted items; positioning, policy-as-code, and declarative seed data deferred as noted.
- **2026-07-12** — **Phase 0 (spike) complete**, stack confirmed: DacFx headless comparison works (`spikes/dacfx-headless`); Native AOT structurally incompatible → plan B proven (self-contained un-trimmed single-file + self-extract, 171 MB; [ADR-0001](docs/adr/0001-csharp-dacfx-foundation.md)); `CREATE OR ALTER` dependency ordering works (`spikes/create-or-alter`).
- **2026-07-12** — **Phase 1 core loop shipped** (commit `8e81aa6`): `inspect` (round-trip verified) / `diff` (plan text + JSON) / `apply` + history ledger (`__SchemorphHistory`, self-excluding) / versioned migrations (normalized SHA-256, run-once, tamper fail-fast) / destructive gating (data-bearing DROPs only) / semantic exit codes (0/1/2). Unit tests 23/23, LocalDB e2e.
