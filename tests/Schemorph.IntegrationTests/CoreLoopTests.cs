using Schemorph.Core.Ledger;
using Schemorph.Core.Migrations;
using Schemorph.Core.Operations;
using Schemorph.Core.Planning;
using Schemorph.Core.Providers;
using Schemorph.Core.Redefine;
using Schemorph.Provider.SqlServer;

namespace Schemorph.IntegrationTests;

/// <summary>
/// The core loop against a real SQL Server, at the seams the CLI wires together.
/// Every test gets a throwaway database (see <see cref="TestDatabase"/>).
/// </summary>
public sealed class CoreLoopTests : IDisposable
{
    private readonly TestDatabase _db = new();
    private readonly string _dir = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), $"schemorph-it-{Guid.NewGuid():N}")).FullName;
    private readonly SqlServerProvider _provider = new();
    private readonly SqlServerLedgerStore _ledger = new();

    private string Write(string relativePath, string content)
    {
        var path = Path.Combine(_dir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    /// <summary>Load-first, mirroring the operations: one desired-state load per call.</summary>
    private async Task<IDesiredState> LoadAsync(string schemaDir)
    {
        var state = await _provider.LoadDesiredStateAsync(schemaDir);
        Assert.Empty(state.Errors);
        return state;
    }

    private async Task<ApplyResult> Apply(
        string schemaDir, bool allowDestructive = false, Action<IReadOnlyList<RawChange>>? onChangesComputed = null) =>
        await _provider.ApplyAsync(new ApplyRequest(await LoadAsync(schemaDir), _db.Url),
            c => PlanBuilder.ShouldInclude(c, allowDestructive), onChangesComputed);

    private async Task<ProgrammableAnalysis> AnalyzeAsync(string schemaDir) =>
        await _provider.AnalyzeProgrammablesAsync(await LoadAsync(schemaDir));

    [SkippableFact]
    public async Task Apply_creates_the_schema_and_a_second_compare_converges_to_zero()
    {
        Write("schema/tables/dbo.Items.sql",
            "CREATE TABLE dbo.Items (Id INT NOT NULL PRIMARY KEY, Name NVARCHAR(50) NOT NULL);\nGO\n");

        // The pre-apply hook reports the plan from the SAME comparison session that
        // executes — before anything hits the database.
        IReadOnlyList<RawChange>? announced = null;
        var tableExistedAtAnnounce = true;
        var result = await Apply(Path.Combine(_dir, "schema"), onChangesComputed: changes =>
        {
            announced = changes;
            tableExistedAtAnnounce = _db.Scalar<int>("SELECT COUNT(*) FROM sys.tables WHERE name = 'Items'") > 0;
        });

        Assert.True(result.Success);
        Assert.NotNull(announced);
        Assert.Contains(announced, c => c.ObjectName == "dbo.Items");
        Assert.False(tableExistedAtAnnounce);
        Assert.Equal(1, _db.Scalar<int>("SELECT COUNT(*) FROM sys.tables WHERE name = 'Items'"));

        var again = await _provider.CompareAsync(
            new CompareRequest(await LoadAsync(Path.Combine(_dir, "schema")), _db.Url));
        Assert.Empty(again.Changes);
    }

    // The safety lint must survive DacFx's own scripting choices. Inserting a
    // NOT NULL column WITHOUT a default mid-table makes DacFx rebuild the table
    // rather than ALTER ADD — the row-copy then omits the new column, so the
    // same "fails on a row-holding table" hazard is present. This drives the
    // REAL generated rebuild through the plan+lint path (the hand-written
    // attributor unit test cannot catch DacFx changing its rebuild shape), so
    // SCHEMORPH101 must fire alongside the rebuild-cost SCHEMORPH102.
    [SkippableFact]
    public async Task Rebuild_that_adds_a_not_null_without_default_column_lints_SCHEMORPH101()
    {
        _db.Execute("CREATE TABLE dbo.Cat (Id INT NOT NULL PRIMARY KEY, Tail INT NOT NULL)");
        _db.Execute("INSERT INTO dbo.Cat (Id, Tail) VALUES (1, 10)");   // a row to fail on
        Write("schema/tables/dbo.Cat.sql",
            "CREATE TABLE dbo.Cat (Id INT NOT NULL PRIMARY KEY, Middle NVARCHAR(30) NOT NULL, Tail INT NOT NULL);\nGO\n");

        var compared = await _provider.CompareAsync(
            new CompareRequest(await LoadAsync(Path.Combine(_dir, "schema")), _db.Url));
        var plan = PlanBuilder.Build(compared, allowDestructive: false);

        var change = Assert.Single(plan.Actions, a => a.ObjectName == "dbo.Cat");
        Assert.NotNull(change.Sql);
        Assert.Contains("tmp_ms_xx", change.Sql);   // confirms DacFx chose a rebuild
        Assert.Contains(plan.Messages, m => m.Code == "SCHEMORPH101");
        Assert.Contains(plan.Messages, m => m.Code == "SCHEMORPH102");
    }

    [SkippableFact]
    public async Task Inspect_exports_the_conventional_layout_and_self_excludes_the_ledger()
    {
        _db.Execute("CREATE TABLE dbo.T (Id INT NOT NULL PRIMARY KEY)");
        _db.Execute("CREATE PROCEDURE dbo.P AS SELECT 1");
        await _ledger.EnsureInitializedAsync(_db.Url);

        var result = await InspectOperation.RunAsync(_provider, _db.Url, Path.Combine(_dir, "out"));

        var names = result.WrittenFiles.Select(Path.GetFileName).ToList();
        Assert.Contains("dbo.T.sql", names);
        Assert.Contains("dbo.P.sql", names);
        Assert.DoesNotContain(names, n => n!.Contains(LedgerObjects.HistoryTable));
    }

    [SkippableFact]
    public async Task Destructive_drop_needs_the_explicit_flag()
    {
        _db.Execute("CREATE TABLE dbo.Zombie (Id INT NOT NULL PRIMARY KEY)");
        Write("schema/tables/dbo.Keep.sql", "CREATE TABLE dbo.Keep (Id INT NOT NULL PRIMARY KEY);\nGO\n");

        var gated = await Apply(Path.Combine(_dir, "schema"), allowDestructive: false);
        Assert.True(gated.Success);
        Assert.Contains(gated.ExcludedChanges, c => c.ObjectName == "dbo.Zombie");
        Assert.Equal(1, _db.Scalar<int>("SELECT COUNT(*) FROM sys.tables WHERE name = 'Zombie'"));

        var allowed = await Apply(Path.Combine(_dir, "schema"), allowDestructive: true);
        Assert.True(allowed.Success);
        Assert.Equal(0, _db.Scalar<int>("SELECT COUNT(*) FROM sys.tables WHERE name = 'Zombie'"));
    }

    [SkippableFact]
    public async Task Failed_migration_rolls_back_atomically_and_leaves_only_a_failure_row()
    {
        _db.Execute("CREATE TABLE dbo.Data (Id INT NOT NULL PRIMARY KEY)");
        await _ledger.EnsureInitializedAsync(_db.Url);
        var runner = new MigrationRunner(_provider, _ledger);
        var migrations = Directory.CreateDirectory(Path.Combine(_dir, "migrations")).FullName;
        var file = Path.Combine(migrations, "V1__seed.sql");
        File.WriteAllText(file, "INSERT INTO dbo.Data (Id) VALUES (1);\nSELECT 1/0;\n");

        await Assert.ThrowsAnyAsync<Exception>(() => runner.RunAsync(migrations, _db.Url));

        // ADR-0004: the INSERT rolled back with the failed script; only a failure row remains.
        Assert.Equal(0, _db.Scalar<int>("SELECT COUNT(*) FROM dbo.Data"));
        var entry = Assert.Single(await _ledger.ReadAsync(_db.Url, MigrationRunner.LedgerKind));
        Assert.False(entry.Succeeded);

        // A fixed file runs exactly once from then on (convergent re-run).
        File.WriteAllText(file, "INSERT INTO dbo.Data (Id) VALUES (1);\n");
        var fixedRun = await runner.RunAsync(migrations, _db.Url);
        Assert.Equal(new[] { "V1__seed.sql" }, fixedRun.Applied);
        Assert.Equal(1, _db.Scalar<int>("SELECT COUNT(*) FROM dbo.Data"));

        var rerun = await runner.RunAsync(migrations, _db.Url);
        Assert.Empty(rerun.Applied);
        Assert.Equal(1, rerun.Skipped);
    }

    [SkippableFact]
    public async Task Redefines_apply_in_dependency_order_and_skip_when_unchanged()
    {
        // dbo.AProc depends on dbo.ZView: alphabetical order would fail on first apply.
        Write("schema/views/dbo.ZView.sql",
            "CREATE VIEW dbo.ZView AS SELECT 1 AS One;\nGO\n");
        Write("schema/procedures/dbo.AProc.sql",
            "CREATE PROCEDURE dbo.AProc AS SELECT One FROM dbo.ZView;\nGO\n");
        await _ledger.EnsureInitializedAsync(_db.Url);
        var runner = new RedefineRunner(_provider, _ledger);

        var analysis = await AnalyzeAsync(Path.Combine(_dir, "schema"));
        Assert.DoesNotContain(analysis.Messages, m => m.Severity == "Error");
        var run = await runner.RunAsync(analysis, _db.Url);

        Assert.Equal(new[] { "dbo.ZView", "dbo.AProc" }, run.Redefined);
        Assert.Equal(1, _db.Scalar<int>("SELECT COUNT(*) FROM sys.procedures WHERE name = 'AProc'"));

        var second = await runner.RunAsync(analysis, _db.Url);
        Assert.Empty(second.Redefined);
        Assert.Equal(2, second.Skipped);
    }

    [SkippableFact]
    public async Task Brownfield_objects_matching_their_files_reconcile_instead_of_redefining()
    {
        // ADR-0002 addendum: a database Schemorph did not build. The view exists,
        // deployed by another tool; the file says the same thing.
        _db.Execute("CREATE VIEW dbo.Existing AS SELECT 1 AS One");
        Write("schema/views/dbo.Existing.sql",
            "CREATE VIEW dbo.Existing AS SELECT 1 AS One;\nGO\n");
        await _ledger.EnsureInitializedAsync(_db.Url);
        var runner = new RedefineRunner(_provider, _ledger);
        var analysis = await AnalyzeAsync(Path.Combine(_dir, "schema"));

        // diff view: nothing pending — no phantom redefine on adoption.
        var plan = await runner.PlanAsync(analysis, _db.Url);
        Assert.Empty(plan.Pending);
        Assert.Equal(new[] { "dbo.Existing" }, plan.Reconcilable.Select(o => o.ObjectName));

        // apply view: recorded, nothing executed, and steady state from then on.
        var run = await runner.RunAsync(analysis, _db.Url);
        Assert.Empty(run.Redefined);
        Assert.Equal(new[] { "dbo.Existing" }, run.Reconciled);
        var entry = Assert.Single(await _ledger.ReadAsync(_db.Url, RedefineRunner.LedgerKind));
        Assert.Equal("Reconcile", entry.Operation);

        var steady = await runner.PlanAsync(analysis, _db.Url);
        Assert.Empty(steady.Pending);
        Assert.Empty(steady.Reconcilable);

        // An edit after adoption is a normal pending redefine — edits always win.
        Write("schema/views/dbo.Existing.sql",
            "CREATE VIEW dbo.Existing AS SELECT 2 AS Two;\nGO\n");
        var edited = await AnalyzeAsync(Path.Combine(_dir, "schema"));
        var afterEdit = await runner.PlanAsync(edited, _db.Url);
        Assert.Equal(new[] { "dbo.Existing" }, afterEdit.Pending.Select(o => o.Object.ObjectName));
    }

    [SkippableFact]
    public async Task Brownfield_objects_differing_from_their_files_stay_pending()
    {
        _db.Execute("CREATE VIEW dbo.Drifted AS SELECT 1 AS One");
        Write("schema/views/dbo.Drifted.sql",
            "CREATE VIEW dbo.Drifted AS SELECT 99 AS NinetyNine;\nGO\n");
        await _ledger.EnsureInitializedAsync(_db.Url);
        var runner = new RedefineRunner(_provider, _ledger);
        var analysis = await AnalyzeAsync(Path.Combine(_dir, "schema"));

        var plan = await runner.PlanAsync(analysis, _db.Url);

        Assert.Equal(new[] { "dbo.Drifted" }, plan.Pending.Select(o => o.Object.ObjectName));
        Assert.Empty(plan.Reconcilable);
    }

    [SkippableFact]
    public async Task Apply_gate_rejects_a_stale_fingerprint_and_accepts_the_current_one()
    {
        Write("schema/tables/dbo.Gated.sql",
            "CREATE TABLE dbo.Gated (Id INT NOT NULL PRIMARY KEY);\nGO\n");
        var schemaDir = Path.Combine(_dir, "schema");

        // A stale hash (reviewed against a different plan) must abort with nothing applied.
        var rejected = await Schemorph.Core.Operations.ApplyOperation.RunAsync(
            _provider, _ledger, new Schemorph.Core.Operations.ApplyOperation.Request(
                schemaDir, _db.Url, ExpectedPlanHash: new string('0', 64)));
        Assert.False(rejected.Success);
        Assert.Equal(Schemorph.Core.Operations.ApplyOperation.FailureStage.PlanMismatch, rejected.Stage);
        Assert.Equal(0, _db.Scalar<int>("SELECT COUNT(*) FROM sys.tables WHERE name = 'Gated'"));

        // The gate's own fingerprint (from the rejected outcome's computed plan) applies cleanly.
        var expected = PlanFingerprint.Compute(rejected.Plan!);
        var accepted = await Schemorph.Core.Operations.ApplyOperation.RunAsync(
            _provider, _ledger, new Schemorph.Core.Operations.ApplyOperation.Request(
                schemaDir, _db.Url, ExpectedPlanHash: expected));
        Assert.True(accepted.Success);
        Assert.Equal(1, _db.Scalar<int>("SELECT COUNT(*) FROM sys.tables WHERE name = 'Gated'"));
    }

    public void Dispose()
    {
        _db.Dispose();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }
}
