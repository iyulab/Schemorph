# Contributing to Schemorph

Thanks for your interest. Schemorph is in early design; right now the highest-value contributions are on the *shape* of the tool, not volume of code.

## Ground rules

- **Read [`docs/design-principles.md`](./docs/design-principles.md) first.** It defines what is fixed. Proposals that conflict with an anchor need to argue for changing the anchor (via ADR), not route around it.
- **ADR-driven decisions.** Anything that constrains future work — provider boundary changes, plan-format changes, ledger semantics, new user-facing concepts — gets an Architecture Decision Record in [`docs/adr/`](./docs/adr/) before or alongside the implementation PR. Small implementation choices do not; use judgment, and when unsure, ask in an issue first.
- **Discuss before large PRs.** Open an issue describing the problem and intended approach. This protects your time more than ours.

## Writing an ADR

Copy the structure of an existing ADR: Status, Date, Context (the forces at play), Decision (what and why), Consequences (positive *and* negative — an ADR without accepted risks is usually incomplete), Alternatives considered.

## Development

Requires the .NET 10 SDK. SQL Server LocalDB (or any reachable SQL Server) is needed for end-to-end runs.

```bash
dotnet build Schemorph.slnx        # build everything
dotnet test                        # unit tests (core semantics)
dotnet run --project src/Schemorph.Cli -- help
```

Layout: `src/Schemorph.Core` (plan model, strategies, ledger contract), `src/Schemorph.Provider.SqlServer` (DacFx-based provider), `src/Schemorph.Cli`, `tests/`. The `spikes/` directory holds the Phase 0 validation spikes referenced by the ADRs; they are kept as executable evidence and a seed for the regression corpus.

Expectations once code exists:

- Tests accompany behavior. Comparison and plan-generation logic is tested against real database instances in containers, not only mocks.
- Public JSON output shapes are contracts; changing them requires versioning and a note in the changelog.
- Safety-relevant behavior (destructive gating, checksum verification, ledger writes) gets the strictest review.

## Conduct

Be kind, be direct, assume good faith. Disagreements are settled by argument quality against the design principles, not by volume or seniority.

## License

Contributions are accepted under the project's MIT license.
