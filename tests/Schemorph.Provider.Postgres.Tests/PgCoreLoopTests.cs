using Npgsql;
using Schemorph.Core.Operations;
using Schemorph.Core.Providers;

namespace Schemorph.Provider.Postgres.Tests;

/// <summary>
/// The declared scope, exercised through the CORE orchestration — the same
/// DiffOperation/ApplyOperation every surface renders. This is the contract
/// layer of parity with the first provider: identical operations, plan vocabulary and ledger
/// semantics, with the provider only supplying dialect (ADR-0003).
/// </summary>
public class PgCoreLoopTests : IAsyncLifetime
{
    private PgTestSchema _live = null!;
    private string _url = null!;
    private string _schemaDir = null!;
    private readonly PostgresProvider _provider = new();
    private readonly PostgresLedgerStore _ledger = new();

    private const string LiveV1 = """
        CREATE TABLE "Workspaces" (
            "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
            "Name" text NOT NULL,
            CONSTRAINT "PK_Workspaces" PRIMARY KEY ("Id")
        );
        """;

    public async Task InitializeAsync()
    {
        _live = await PgTestSchema.CreateAsync(LiveV1);
        _url = new NpgsqlConnectionStringBuilder(PgTestSchema.ServerUrl!) { SearchPath = _live.Name }
            .ConnectionString;

        // The desired state: v1 plus a column, a CHECK, and a new table — the
        // files a user would keep in their repo, qualified with their schema.
        _schemaDir = Path.Combine(Path.GetTempPath(), "schemorph-pg-loop-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(Path.Combine(_schemaDir, "tables"));
        await File.WriteAllTextAsync(Path.Combine(_schemaDir, "tables", "Workspaces.sql"), $"""
            CREATE TABLE "{_live.Name}"."Workspaces" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "Name" text NOT NULL,
                "Tier" text NOT NULL DEFAULT 'free',
                CONSTRAINT "PK_Workspaces" PRIMARY KEY ("Id"),
                CONSTRAINT "CK_Tier" CHECK ("Tier" IN ('free', 'pro'))
            );
            """);
        await File.WriteAllTextAsync(Path.Combine(_schemaDir, "tables", "Members.sql"), $"""
            CREATE TABLE "{_live.Name}"."Members" (
                "Id" uuid NOT NULL,
                "WorkspaceId" uuid NOT NULL,
                CONSTRAINT "PK_Members" PRIMARY KEY ("Id"),
                CONSTRAINT "FK_Members_Workspaces" FOREIGN KEY ("WorkspaceId")
                    REFERENCES "{_live.Name}"."Workspaces" ("Id")
            );
            """);
        // Seed DML is not desired state — classified out with a warning, the
        // SQL Server convention.
        await File.WriteAllTextAsync(Path.Combine(_schemaDir, "seed.sql"),
            "INSERT INTO \"Workspaces\" (\"Name\") VALUES ('demo');");
    }

    public async Task DisposeAsync()
    {
        await _live.DisposeAsync();
        try { Directory.Delete(_schemaDir, recursive: true); } catch { }
    }

    [SkippableFact]
    public async Task Diff_apply_rediff_converges_with_the_gate_and_the_ledger()
    {
        // diff: two table changes, a partial-atomicity plan, an executable script.
        var diff = await DiffOperation.RunAsync(_provider, _ledger, _schemaDir, _url, allowDestructive: false);
        Assert.True(diff.Success, string.Join("; ", diff.Errors.Select(e => e.Text)));
        var plan = diff.Plan!;
        Assert.Equal(ApplyAtomicity.Partial, plan.Atomicity);
        Assert.Equal(2, plan.Actions.Count);
        Assert.Contains(plan.Messages, m => m.Code == "SCHEMORPH006");   // the seed file, loudly skipped
        Assert.NotNull(diff.UpdateScript);
        Assert.Contains("SET LOCAL search_path", diff.UpdateScript);

        // apply, gated on the reviewed plan's fingerprint.
        var expected = Schemorph.Core.Planning.PlanFingerprint.Compute(plan);
        var outcome = await ApplyOperation.RunAsync(_provider, _ledger,
            new ApplyOperation.Request(_schemaDir, _url, ExpectedPlanHash: expected));
        Assert.True(outcome.Success, string.Join("; ", outcome.Errors.Select(e => e.Text)));
        Assert.Equal(2, outcome.Applied.Count);

        // the ledger records what applied, in the core's own vocabulary.
        var recorded = await _ledger.ReadAsync(_url, "declarative");
        Assert.Equal(2, recorded.Count);
        Assert.All(recorded, e => Assert.True(e.Succeeded));

        // re-diff: empty — the convergence contract, through the same operation.
        var rediff = await DiffOperation.RunAsync(_provider, _ledger, _schemaDir, _url, allowDestructive: false);
        Assert.True(rediff.Success);
        Assert.False(rediff.Plan!.HasChanges);
    }

    [SkippableFact]
    public async Task A_stale_plan_hash_refuses_before_anything_runs()
    {
        var outcome = await ApplyOperation.RunAsync(_provider, _ledger,
            new ApplyOperation.Request(_schemaDir, _url, ExpectedPlanHash: new string('0', 64)));

        Assert.False(outcome.Success);
        Assert.Equal(ApplyOperation.FailureStage.PlanMismatch, outcome.Stage);

        var live = await CatalogReader.ReadTablesAsync(PgTestSchema.ServerUrl!, _live.Name);
        Assert.DoesNotContain(live, t => t.Name == "Members");   // nothing executed
    }

    /// <summary>
    /// The live proof behind the `atomicity: partial` declaration (ADR-0007's
    /// 2026-08-19 addendum): a declarative change and a redefine failure in the
    /// SAME apply — the table from the declarative stage stays committed even
    /// though the overall apply reports failure. `transactional` would mean
    /// this cannot happen; it does, because the two stages do not share a
    /// transaction boundary.
    /// </summary>
    [SkippableFact]
    public async Task A_redefine_failure_leaves_the_already_committed_declarative_stage_in_place()
    {
        var schemaDir = Path.Combine(Path.GetTempPath(), "schemorph-pg-partial-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(Path.Combine(schemaDir, "tables"));
        Directory.CreateDirectory(Path.Combine(schemaDir, "views"));
        try
        {
            await File.WriteAllTextAsync(Path.Combine(schemaDir, "tables", "Widgets.sql"), $"""
                CREATE TABLE "{_live.Name}"."Widgets" (
                    "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                    "Name" text NOT NULL,
                    CONSTRAINT "PK_Widgets" PRIMARY KEY ("Id")
                );
                """);
            // Syntactically valid, semantically broken: the parser accepts it (no
            // column-existence check), so this only fails when the database
            // actually executes the CREATE OR REPLACE — exactly the failure mode
            // that matters here.
            await File.WriteAllTextAsync(Path.Combine(schemaDir, "views", "BrokenView.sql"), """
                CREATE VIEW "BrokenView" AS SELECT "Id", "NoSuchColumn" FROM "Widgets";
                """);

            var diff = await DiffOperation.RunAsync(_provider, _ledger, schemaDir, _url, allowDestructive: false);
            Assert.True(diff.Success, string.Join("; ", diff.Errors.Select(e => e.Text)));
            var expected = Schemorph.Core.Planning.PlanFingerprint.Compute(diff.Plan!);

            var outcome = await ApplyOperation.RunAsync(_provider, _ledger,
                new ApplyOperation.Request(schemaDir, _url, ExpectedPlanHash: expected));

            Assert.False(outcome.Success);
            Assert.Equal(ApplyOperation.FailureStage.Redefine, outcome.Stage);
            Assert.Single(outcome.Applied);   // the declarative stage committed...

            var live = await CatalogReader.ReadTablesAsync(PgTestSchema.ServerUrl!, _live.Name);
            Assert.Contains(live, t => t.Name == "Widgets");   // ...and it is still there.
        }
        finally
        {
            try { Directory.Delete(schemaDir, recursive: true); } catch { }
        }
    }
}
