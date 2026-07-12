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
dotnet test                        # unit tests; integration tests skip without a database
dotnet run --project src/Schemorph.Cli -- help
```

Integration tests (`tests/Schemorph.IntegrationTests`) run against a real SQL Server
when `SCHEMORPH_TEST_URL` points at one (its `Initial Catalog` is ignored — each test
creates and drops a throwaway database). Locally, LocalDB works:

```
SCHEMORPH_TEST_URL=Data Source=(localdb)\MSSQLLocalDB;Integrated Security=True;Encrypt=False
```

CI runs them against a SQL Server service container (`.github/workflows/ci.yml`).

Layout: `src/Schemorph.Core` (plan model, strategies, ledger contract), `src/Schemorph.Provider.SqlServer` (DacFx-based provider), `src/Schemorph.Cli`, `tests/`. The `spikes/` directory holds the Phase 0 validation spikes referenced by the ADRs; they are kept as executable evidence and a seed for the regression corpus.

### DacFx version policy

The diff engine is `Microsoft.SqlServer.DacFx`, pinned to an exact version in
`Schemorph.Provider.SqlServer.csproj`. DacFx releases roughly quarterly and its
`SchemaComparison` edge-case behavior is this tool's most plausible regression
surface, so upgrades are deliberate, never automatic:

1. Bump the pin in a dedicated PR.
2. Run the golden corpus (`tests/Schemorph.IntegrationTests/Corpus/`) against a real
   database. Any baseline change is reviewed as a behavior change, not noise — if the
   new behavior is correct, re-freeze the baseline in the same PR with an explanation.
3. Only then does the upgrade merge. When a scenario reveals new engine behavior worth
   guarding, add it to the corpus (a missing `expected.txt` bootstraps itself on first
   run, fails once, and freezes on review).

Expectations once code exists:

- Tests accompany behavior. Comparison and plan-generation logic is tested against real database instances in containers, not only mocks.
- Public JSON output shapes are contracts; changing them requires versioning and a note in the changelog.
- Safety-relevant behavior (destructive gating, checksum verification, ledger writes) gets the strictest review.

## Conduct

Be kind, be direct, assume good faith. Disagreements are settled by argument quality against the design principles, not by volume or seniority.

## License

Contributions are accepted under the project's MIT license.
