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
        string schemaDir, bool allowDestructive = false, Action<CompareResult>? onChangesComputed = null) =>
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
        var result = await Apply(Path.Combine(_dir, "schema"), onChangesComputed: computed =>
        {
            announced = computed.Changes;
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

    // ADR-0006: a principal that lives outside the model (an app login, a
    // backup-operator account) must not be dropped just because the desired state
    // is silent about it. Before the fix DacFx planned DROP USER as a
    // non-destructive change, so a plain column edit would delete the app's login.
    [SkippableFact]
    public async Task Principals_absent_from_the_desired_state_are_not_dropped()
    {
        _db.Execute("CREATE USER [schemorph_it_principal] WITHOUT LOGIN");
        _db.Execute("CREATE ROLE [schemorph_it_role]");
        _db.Execute("CREATE TABLE dbo.Keep (Id INT NOT NULL PRIMARY KEY)");
        // Desired state has the table but says nothing about the principals.
        Write("schema/tables/dbo.Keep.sql",
            "CREATE TABLE dbo.Keep (Id INT NOT NULL PRIMARY KEY);\nGO\n");

        var compared = await _provider.CompareAsync(
            new CompareRequest(await LoadAsync(Path.Combine(_dir, "schema")), _db.Url));

        Assert.DoesNotContain(compared.Changes, c => c.ObjectName is "schemorph_it_principal" or "schemorph_it_role");
        Assert.DoesNotContain(compared.Changes,
            c => c.ObjectType is "User" or "Login" or "Role" && c.Operation is "Delete" or "Drop");
    }

    // ADR-0006 sibling default: column order is not meaningful state. Adding a
    // nullable column in a logical mid position must NOT rebuild the table — the
    // whole reason a code-gen consumer saw 7 unrelated tables rebuilt on a single
    // additive change. With IgnoreColumnOrder it is an in-place ALTER TABLE ADD.
    [SkippableFact]
    public async Task Adding_a_column_mid_table_stays_in_place_not_a_rebuild()
    {
        _db.Execute("CREATE TABLE dbo.Ledger (Id INT NOT NULL PRIMARY KEY, Amount INT NOT NULL)");
        _db.Execute("INSERT INTO dbo.Ledger (Id, Amount) VALUES (1, 100)");
        // The new column sits logically between the existing two.
        Write("schema/tables/dbo.Ledger.sql",
            "CREATE TABLE dbo.Ledger (Id INT NOT NULL PRIMARY KEY, Memo NVARCHAR(50) NULL, Amount INT NOT NULL);\nGO\n");

        var compared = await _provider.CompareAsync(
            new CompareRequest(await LoadAsync(Path.Combine(_dir, "schema")), _db.Url));
        var plan = PlanBuilder.Build(compared, allowDestructive: false);

        var change = Assert.Single(plan.Actions, a => a.ObjectName == "dbo.Ledger");
        Assert.NotNull(change.Sql);
        Assert.DoesNotContain("tmp_ms_xx", change.Sql);   // no rebuild
        Assert.Contains("ADD", change.Sql, StringComparison.OrdinalIgnoreCase);   // in-place ALTER ADD
        Assert.DoesNotContain(plan.Messages, m => m.Code == "SCHEMORPH102");   // no rebuild-cost warning
    }

    // The safety lint must survive DacFx's own scripting choices. A widened
    // primary key forces a genuine table rebuild (independent of column order,
    // which Schemorph now ignores), and the new NOT NULL-without-default column
    // is omitted from the row-copy INSERT — the same "fails on a row-holding
    // table" hazard. This drives the REAL generated rebuild through the plan+lint
    // path (the hand-written attributor unit test cannot catch DacFx changing its
    // rebuild shape), so SCHEMORPH101 must fire alongside the rebuild-cost
    // SCHEMORPH102.
    [SkippableFact]
    public async Task Rebuild_that_adds_a_not_null_without_default_column_lints_SCHEMORPH101()
    {
        _db.Execute("CREATE TABLE dbo.Cat (Id INT NOT NULL PRIMARY KEY, Tail INT NOT NULL)");
        _db.Execute("INSERT INTO dbo.Cat (Id, Tail) VALUES (1, 10)");   // a row to fail on
        // Widening the PK to (Id, Tail) forces a rebuild; Middle is a new NOT NULL
        // column with no default that the row-copy cannot carry.
        Write("schema/tables/dbo.Cat.sql",
            "CREATE TABLE dbo.Cat (Id INT NOT NULL, Tail INT NOT NULL, Middle NVARCHAR(30) NOT NULL, CONSTRAINT PK_Cat PRIMARY KEY (Id, Tail));\nGO\n");

        var compared = await _provider.CompareAsync(
            new CompareRequest(await LoadAsync(Path.Combine(_dir, "schema")), _db.Url));
        var plan = PlanBuilder.Build(compared, allowDestructive: false);

        var change = Assert.Single(plan.Actions, a => a.ObjectName == "dbo.Cat");
        Assert.NotNull(change.Sql);
        Assert.Contains("tmp_ms_xx", change.Sql);   // confirms DacFx chose a rebuild
        Assert.Contains(plan.Messages, m => m.Code == "SCHEMORPH101");
        Assert.Contains(plan.Messages, m => m.Code == "SCHEMORPH102");
    }

    // The ALTER-ADD shape of the same hazard still lints: adding a NOT NULL column
    // without a default now stays an in-place ALTER TABLE ADD (column order
    // ignored, so no rebuild), and SCHEMORPH101 fires without the rebuild-cost
    // SCHEMORPH102 — the lint follows the hazard, not the scripting shape.
    [SkippableFact]
    public async Task In_place_add_of_a_not_null_without_default_column_lints_SCHEMORPH101_only()
    {
        _db.Execute("CREATE TABLE dbo.Dog (Id INT NOT NULL PRIMARY KEY, Tail INT NOT NULL)");
        _db.Execute("INSERT INTO dbo.Dog (Id, Tail) VALUES (1, 10)");   // a row to fail on
        Write("schema/tables/dbo.Dog.sql",
            "CREATE TABLE dbo.Dog (Id INT NOT NULL PRIMARY KEY, Middle NVARCHAR(30) NOT NULL, Tail INT NOT NULL);\nGO\n");

        var compared = await _provider.CompareAsync(
            new CompareRequest(await LoadAsync(Path.Combine(_dir, "schema")), _db.Url));
        var plan = PlanBuilder.Build(compared, allowDestructive: false);

        var change = Assert.Single(plan.Actions, a => a.ObjectName == "dbo.Dog");
        Assert.NotNull(change.Sql);
        Assert.DoesNotContain("tmp_ms_xx", change.Sql);   // in-place, not a rebuild
        Assert.Contains(plan.Messages, m => m.Code == "SCHEMORPH101");
        Assert.DoesNotContain(plan.Messages, m => m.Code == "SCHEMORPH102");
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

    // A column retype leaves a dependent view's text byte-identical, so the
    // redefine checksum sees nothing to do and the declarative diff has nothing to
    // say about the view either — but SQL Server's cached view metadata still
    // describes the old column type. The view then reports int for a bigint
    // column until someone refreshes it (what sp_refreshview papered over under
    // SSDT). Whoever changed the column never asked for a stale view.
    [SkippableFact]
    public async Task Retyping_a_column_redefines_the_views_that_depend_on_it()
    {
        _db.Execute("CREATE TABLE dbo.Bill (Id INT NOT NULL PRIMARY KEY, Amount INT NOT NULL)");
        _db.Execute("CREATE VIEW dbo.BillView AS SELECT b.Id, b.Amount FROM dbo.Bill AS b");
        _db.Execute("CREATE VIEW dbo.BillTotals AS SELECT COUNT(*) AS Bills, SUM(v.Amount) AS Total FROM dbo.BillView AS v");

        // Only the column type changes. Both view files are byte-identical to what
        // is already deployed.
        Write("schema/tables/dbo.Bill.sql",
            "CREATE TABLE dbo.Bill (Id INT NOT NULL PRIMARY KEY, Amount BIGINT NOT NULL);\nGO\n");
        Write("schema/views/dbo.BillView.sql",
            "CREATE VIEW dbo.BillView AS SELECT b.Id, b.Amount FROM dbo.Bill AS b;\nGO\n");
        Write("schema/views/dbo.BillTotals.sql",
            "CREATE VIEW dbo.BillTotals AS SELECT COUNT(*) AS Bills, SUM(v.Amount) AS Total FROM dbo.BillView AS v;\nGO\n");
        var schemaDir = Path.Combine(_dir, "schema");

        // The plan must say so before it runs — a redefinition nobody was told
        // about is the same silence in the other direction.
        var diff = await DiffOperation.RunAsync(_provider, _ledger, schemaDir, _db.Url, allowDestructive: false);
        Assert.Contains(diff.Plan!.Actions, a =>
            a.ObjectName == "dbo.BillView" && a.Operation == PlanOperation.Redefine);

        var outcome = await ApplyOperation.RunAsync(
            _provider, _ledger, new ApplyOperation.Request(schemaDir, _db.Url));
        Assert.True(outcome.Success);

        // The view's own metadata must now describe the new type. bigint is 8
        // bytes, int is 4 — a stale view still reports 4.
        Assert.Equal((short)8, _db.Scalar<short>("""
            SELECT c.max_length FROM sys.columns c
            JOIN sys.views v ON v.object_id = c.object_id
            WHERE v.name = 'BillView' AND c.name = 'Amount'
            """));
        // ... and so must a view that only depends on it transitively.
        Assert.Equal((short)8, _db.Scalar<short>("""
            SELECT c.max_length FROM sys.columns c
            JOIN sys.views v ON v.object_id = c.object_id
            WHERE v.name = 'BillTotals' AND c.name = 'Total'
            """));
    }

    // The invalidation must stay targeted: an unrelated additive column change
    // must not drag every view through a redefinition (that would throw away the
    // idempotent-skip and reconcile behaviour ADR-0002 exists for).
    [SkippableFact]
    public async Task An_unrelated_table_change_does_not_redefine_views()
    {
        _db.Execute("CREATE TABLE dbo.Alpha (Id INT NOT NULL PRIMARY KEY)");
        _db.Execute("CREATE TABLE dbo.Beta (Id INT NOT NULL PRIMARY KEY, Amount INT NOT NULL)");
        _db.Execute("CREATE VIEW dbo.BetaView AS SELECT b.Id, b.Amount FROM dbo.Beta AS b");

        // Alpha gains a column; Beta and its view are untouched.
        Write("schema/tables/dbo.Alpha.sql",
            "CREATE TABLE dbo.Alpha (Id INT NOT NULL PRIMARY KEY, Label NVARCHAR(20) NULL);\nGO\n");
        Write("schema/tables/dbo.Beta.sql",
            "CREATE TABLE dbo.Beta (Id INT NOT NULL PRIMARY KEY, Amount INT NOT NULL);\nGO\n");
        Write("schema/views/dbo.BetaView.sql",
            "CREATE VIEW dbo.BetaView AS SELECT b.Id, b.Amount FROM dbo.Beta AS b;\nGO\n");
        var schemaDir = Path.Combine(_dir, "schema");

        // First apply reconciles the view (its live definition matches the file).
        await ApplyOperation.RunAsync(_provider, _ledger, new ApplyOperation.Request(schemaDir, _db.Url));

        // Nothing is left to do — in particular the view is not redefined again.
        var diff = await DiffOperation.RunAsync(_provider, _ledger, schemaDir, _db.Url, allowDestructive: false);
        Assert.DoesNotContain(diff.Plan!.Actions, a => a.ObjectName == "dbo.BetaView");
    }

    public void Dispose()
    {
        _db.Dispose();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }
}
