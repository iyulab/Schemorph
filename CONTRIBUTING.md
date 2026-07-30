# Contributing to Schemorph

Thanks for your interest. Schemorph is in early design; right now the highest-value contributions are on the *shape* of the tool, not volume of code.

## Ground rules

- **Read [`docs/design-principles.md`](./docs/design-principles.md) first.** It defines what is fixed. Proposals that conflict with an anchor need to argue for changing the anchor (via ADR), not route around it.
- **ADR-driven decisions.** Anything that constrains future work — provider boundary changes, plan-format changes, ledger semantics, new user-facing concepts — gets an Architecture Decision Record in [`docs/adr/`](./docs/adr/) before or alongside the implementation PR. Small implementation choices do not; use judgment, and when unsure, ask in an issue first.
- **Discuss before large PRs.** Open an issue describing the problem and intended approach. This protects your time more than ours.

## Writing an ADR

Copy the structure of an existing ADR: Status, Date, Context (the forces at play), Decision (what and why), Consequences (positive *and* negative — an ADR without accepted risks is usually incomplete), Alternatives considered.

## Development

Requires the .NET 10 SDK. A reachable database of the provider you are touching is needed for end-to-end runs; both are available as containers (below).

```bash
dotnet build Schemorph.slnx        # build everything
dotnet test                        # unit tests; live tests skip without a database
dotnet run --project src/Schemorph.Cli -- help
```

### Running the live tests

Every suite that needs a database **skips** when its connection variable is unset, so
`dotnet test` is green on a machine with neither engine. Green-because-skipped is not
green: check the skip count before believing a run, because the variables are what
decide whether a provider was exercised at all.

**SQL Server** — `SCHEMORPH_TEST_URL` drives `tests/Schemorph.IntegrationTests`
(its `Initial Catalog` is ignored; each test class creates and drops a throwaway
database). LocalDB works where it is installed:

```
SCHEMORPH_TEST_URL=Data Source=(localdb)\MSSQLLocalDB;Integrated Security=True;Encrypt=False
```

Everywhere else — any non-Windows machine, and Windows without LocalDB — a throwaway
container is equivalent, and closer to what CI runs:

```bash
docker run -d --name schemorph-mssql -e ACCEPT_EULA=Y -e MSSQL_SA_PASSWORD='<password>' \
  -p 11433:1433 mcr.microsoft.com/mssql/server:2022-latest
# SCHEMORPH_TEST_URL=Server=localhost,11433;User ID=sa;Password=<password>;Encrypt=False;TrustServerCertificate=True
```

**PostgreSQL** — `SCHEMORPH_PG_TEST_URL` drives
`tests/Schemorph.Provider.Postgres.Tests` (a throwaway schema per test) and the
Postgres half of the integration suite:

```bash
docker run -d --name schemorph-postgres -e POSTGRES_PASSWORD='<password>' \
  -p 55432:5432 postgres:16-alpine
docker exec schemorph-postgres psql -U postgres \
  -c "CREATE ROLE schemorph_local LOGIN PASSWORD '<password>' NOSUPERUSER NOCREATEDB NOCREATEROLE;" \
  -c "CREATE DATABASE schemorph_local OWNER schemorph_local;"
# SCHEMORPH_PG_TEST_URL=Host=localhost;Port=55432;Username=schemorph_local;Password=<password>;Database=schemorph_local
```

Point it at the **restricted role**, not the superuser. The provider's requirement is a
managed, non-superuser database with no extension dependency, and CI runs the suite as
exactly such a role — a local superuser run can pass where CI fails, which makes local
green mean less than it appears to.

The integration suite is serial by construction (it drives real databases and child
processes against one server), so budget minutes, not seconds, for a full run. CI runs
both engines as service containers (`.github/workflows/ci.yml`).

Layout: `src/Schemorph.Core` (plan model, strategies, ledger contract), `src/Schemorph.Provider.SqlServer` (DacFx-based provider), `src/Schemorph.Provider.Postgres` (catalog-based provider), `src/Schemorph.Cli`, `tests/`. The `spikes/` directory holds the Phase 0 validation spikes referenced by the ADRs; they are kept as executable evidence and a seed for the regression corpus.

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
