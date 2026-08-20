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
        // diff: two table changes, a transactional-atomicity plan, an executable script.
        var diff = await DiffOperation.RunAsync(_provider, _ledger, _schemaDir, _url, allowDestructive: false);
        Assert.True(diff.Success, string.Join("; ", diff.Errors.Select(e => e.Text)));
        var plan = diff.Plan!;
        Assert.Equal(ApplyAtomicity.Transactional, plan.Atomicity);
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
    /// The live proof behind `atomicity: transactional` (ADR-0004's 2026-08-20
    /// addendum): a declarative change and a redefine failure in the SAME
    /// apply — the table from the declarative stage is GONE after the
    /// failure, because both stages ran inside the one session this provider
    /// opens and it rolled back as a unit. Replaces the `partial`-era proof
    /// this same scenario used to demonstrate (cycle-112), now that the
    /// opposite is true.
    /// </summary>
    [SkippableFact]
    public async Task A_redefine_failure_rolls_back_the_already_applied_declarative_stage()
    {
        var schemaDir = Path.Combine(Path.GetTempPath(), "schemorph-pg-txn-" + Guid.NewGuid().ToString("n")[..8]);
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
            await File.WriteAllTextAsync(Path.Combine(schemaDir, "views", "BrokenView.sql"), """
                CREATE VIEW "BrokenView" AS SELECT "Id", "NoSuchColumn" FROM "Widgets";
                """);

            var diff = await DiffOperation.RunAsync(_provider, _ledger, schemaDir, _url, allowDestructive: false);
            Assert.True(diff.Success, string.Join("; ", diff.Errors.Select(e => e.Text)));
            Assert.Equal(ApplyAtomicity.Transactional, diff.Plan!.Atomicity);
            var expected = Schemorph.Core.Planning.PlanFingerprint.Compute(diff.Plan!);

            var outcome = await ApplyOperation.RunAsync(_provider, _ledger,
                new ApplyOperation.Request(schemaDir, _url, ExpectedPlanHash: expected));

            Assert.False(outcome.Success);
            Assert.Equal(ApplyOperation.FailureStage.Redefine, outcome.Stage);
            Assert.Single(outcome.Applied);   // the outcome still names what the rolled-back transaction attempted...

            var live = await CatalogReader.ReadTablesAsync(PgTestSchema.ServerUrl!, _live.Name);
            Assert.DoesNotContain(live, t => t.Name == "Widgets");   // ...but the rollback took it back out.

            var declarative = await _ledger.ReadAsync(_url, "declarative");
            Assert.DoesNotContain(declarative, e => e.ObjectName.Contains("Widgets"));   // no orphaned success row either

            var redefineFailures = await _ledger.ReadAsync(_url, "redefine");
            Assert.Contains(redefineFailures, e => !e.Succeeded);   // the failure row survives the rollback (ADR-0004 decision 4)
        }
        finally
        {
            try { Directory.Delete(schemaDir, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// The stronger guarantee `transactional` adds over `partial`: a failure
    /// in the LAST stage (migration) rolls back an EARLIER stage (redefine)
    /// that had already succeeded on its own terms — something `partial`
    /// never did, because each stage committed independently there.
    /// </summary>
    [SkippableFact]
    public async Task A_migration_failure_rolls_back_the_declarative_and_redefine_stages_too()
    {
        var schemaDir = Path.Combine(Path.GetTempPath(), "schemorph-pg-txn-mig-" + Guid.NewGuid().ToString("n")[..8]);
        var migrationsDir = Path.Combine(schemaDir, "migrations");
        Directory.CreateDirectory(Path.Combine(schemaDir, "tables"));
        Directory.CreateDirectory(Path.Combine(schemaDir, "views"));
        Directory.CreateDirectory(migrationsDir);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(schemaDir, "tables", "Gadgets.sql"), $"""
                CREATE TABLE "{_live.Name}"."Gadgets" (
                    "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                    CONSTRAINT "PK_Gadgets" PRIMARY KEY ("Id")
                );
                """);
            await File.WriteAllTextAsync(Path.Combine(schemaDir, "views", "GadgetsView.sql"), $"""
                CREATE VIEW "GadgetsView" AS SELECT "Id" FROM "Gadgets";
                """);
            // Fails on execution (unknown table), so the pipeline stops here —
            // declarative and redefine must already have succeeded by then.
            await File.WriteAllTextAsync(Path.Combine(migrationsDir, "V1__boom.sql"),
                "INSERT INTO \"NoSuchTable\" (\"Id\") VALUES (1);");

            var diff = await DiffOperation.RunAsync(_provider, _ledger, schemaDir, _url, allowDestructive: false);
            Assert.True(diff.Success, string.Join("; ", diff.Errors.Select(e => e.Text)));
            var expected = Schemorph.Core.Planning.PlanFingerprint.Compute(diff.Plan!);

            var outcome = await ApplyOperation.RunAsync(_provider, _ledger,
                new ApplyOperation.Request(schemaDir, _url, MigrationsDir: migrationsDir, ExpectedPlanHash: expected));

            Assert.False(outcome.Success);
            Assert.Equal(ApplyOperation.FailureStage.Migration, outcome.Stage);

            var live = await CatalogReader.ReadTablesAsync(PgTestSchema.ServerUrl!, _live.Name);
            Assert.DoesNotContain(live, t => t.Name == "Gadgets");   // declarative rolled back...

            var declarative = await _ledger.ReadAsync(_url, "declarative");
            Assert.DoesNotContain(declarative, e => e.ObjectName.Contains("Gadgets"));

            var redefine = await _ledger.ReadAsync(_url, "redefine");
            Assert.DoesNotContain(redefine, e => e.Succeeded);   // ...and so did the (otherwise successful) redefine.

            var migration = await _ledger.ReadAsync(_url, "migration");
            Assert.Contains(migration, e => !e.Succeeded);   // the migration failure row survives.
        }
        finally
        {
            try { Directory.Delete(schemaDir, recursive: true); } catch { }
        }
    }
}
