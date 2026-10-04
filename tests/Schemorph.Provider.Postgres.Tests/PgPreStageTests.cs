using Npgsql;
using Schemorph.Core.Migrations;
using Schemorph.Core.Operations;
using Schemorph.Core.Planning;

namespace Schemorph.Provider.Postgres.Tests;

/// <summary>
/// Pre-stage scripts on PostgreSQL, through the core operations: a rename the desired state cannot
/// express runs before the declarative stage, and the plan is computed as of after it — in diff by a
/// simulation the provider never commits, in apply inside the one session that applies. The live
/// table carries rows and a view reads it, so "the values came along" and "planning read through the
/// session" are both observable rather than assumed.
/// </summary>
public class PgPreStageTests : IAsyncLifetime
{
    private PgTestSchema _live = null!;
    private string _url = null!;
    private string _schemaDir = null!;
    private string _preDir = null!;
    private readonly PostgresProvider _provider = new();
    private readonly PostgresLedgerStore _ledger = new();

    private const string LiveV1 = """
        CREATE TABLE "Assets" (
            "Id" integer NOT NULL,
            "Name" text NOT NULL,
            CONSTRAINT "PK_Assets" PRIMARY KEY ("Id")
        );
        INSERT INTO "Assets" VALUES (1, 'pump'), (2, 'valve');
        CREATE VIEW "AssetNames" AS SELECT "Name" FROM "Assets";
        """;

    public async Task InitializeAsync()
    {
        _live = await PgTestSchema.CreateAsync(LiveV1);
        _url = new NpgsqlConnectionStringBuilder(PgTestSchema.ServerUrl!) { SearchPath = _live.Name }.ConnectionString;

        var root = Path.Combine(Path.GetTempPath(), "schemorph-pg-pre-" + Guid.NewGuid().ToString("n")[..8]);
        _schemaDir = Path.Combine(root, "schema");
        _preDir = Path.Combine(root, "pre");
        Directory.CreateDirectory(Path.Combine(_schemaDir, "tables"));
        Directory.CreateDirectory(Path.Combine(_schemaDir, "views"));
        Directory.CreateDirectory(_preDir);

        // The desired state names the table by its new name — and the view reads it by that name.
        await File.WriteAllTextAsync(Path.Combine(_schemaDir, "tables", "Equipment.sql"), $"""
            CREATE TABLE "{_live.Name}"."Equipment" (
                "Id" integer NOT NULL,
                "Name" text NOT NULL,
                CONSTRAINT "PK_Equipment" PRIMARY KEY ("Id")
            );
            """);
        await File.WriteAllTextAsync(Path.Combine(_schemaDir, "views", "AssetNames.sql"), $"""
            CREATE VIEW "{_live.Name}"."AssetNames" AS SELECT "Name" FROM "{_live.Name}"."Equipment";
            """);
        // The rename itself — what a diff cannot infer.
        await File.WriteAllTextAsync(Path.Combine(_preDir, "V1__rename_assets.sql"), """
            ALTER TABLE "Assets" RENAME TO "Equipment";
            ALTER TABLE "Equipment" RENAME CONSTRAINT "PK_Assets" TO "PK_Equipment";
            """);
    }

    public async Task DisposeAsync()
    {
        await _live.DisposeAsync();
        try { Directory.Delete(Path.GetDirectoryName(_schemaDir)!, recursive: true); } catch { }
    }

    private async Task<T?> ScalarAsync<T>(string sql)
    {
        await using var connection = new NpgsqlConnection(_url);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        var value = await command.ExecuteScalarAsync();
        return value is DBNull or null ? default : (T)value;
    }

    private Task<bool> ExistsAsync(string relation) =>
        ScalarAsync<bool>($"SELECT to_regclass('\"{_live.Name}\".\"{relation}\"') IS NOT NULL");

    private Task<DiffOperation.DiffResult> DiffAsync(bool withPre) =>
        DiffOperation.RunAsync(_provider, _ledger, _schemaDir, _url, allowDestructive: false,
            preDir: withPre ? _preDir : null);

    [SkippableFact]
    public async Task Diff_plans_as_of_after_the_pre_stage_and_leaves_nothing_behind()
    {
        // The contrast: without the pre-stage the same desired state is a new table and a drop.
        var without = await DiffAsync(withPre: false);
        Assert.True(without.Success, string.Join("; ", without.Errors.Select(e => e.Text)));
        Assert.Contains(without.Plan!.Actions, a => a.ObjectName.EndsWith("Equipment") && a.Operation == PlanOperation.Create);

        var with = await DiffAsync(withPre: true);
        Assert.True(with.Success, string.Join("; ", with.Errors.Select(e => e.Text)));
        Assert.DoesNotContain(with.Plan!.Actions, a => a.ObjectType == "Table");
        var pre = Assert.Single(with.Plan.PreScripts);
        Assert.Equal("V1__rename_assets.sql", pre.FileName);
        Assert.NotEqual(PlanFingerprint.Compute(without.Plan), PlanFingerprint.Compute(with.Plan));

        // Read-only: the rename was undone, and nothing of Schemorph's was written.
        Assert.True(await ExistsAsync("Assets"));
        Assert.False(await ExistsAsync("Equipment"));
        Assert.Empty(await _ledger.ReadAsync(_url, VersionedStage.Pre.LedgerKind));
    }

    [SkippableFact]
    public async Task Apply_runs_the_pre_stage_first_and_the_rows_come_along()
    {
        var diff = await DiffAsync(withPre: true);
        Assert.True(diff.Success, string.Join("; ", diff.Errors.Select(e => e.Text)));
        var expected = PlanFingerprint.Compute(diff.Plan!);

        var outcome = await ApplyOperation.RunAsync(_provider, _ledger,
            new ApplyOperation.Request(_schemaDir, _url, PreDir: _preDir, ExpectedPlanHash: expected));

        Assert.True(outcome.Success, string.Join("; ", outcome.Errors.Select(e => e.Text)));
        Assert.Equal(new[] { "V1__rename_assets.sql" }, outcome.PreScripts!.Applied);
        Assert.Equal(2L, await ScalarAsync<long>($"SELECT count(*) FROM \"{_live.Name}\".\"Equipment\""));
        Assert.False(await ExistsAsync("Assets"));
        Assert.Equal(2L, await ScalarAsync<long>($"SELECT count(*) FROM \"{_live.Name}\".\"AssetNames\""));
        var recorded = Assert.Single(await _ledger.ReadAsync(_url, VersionedStage.Pre.LedgerKind));
        Assert.Equal("V1__rename_assets.sql", recorded.ObjectName);

        // Converged: nothing pending, nothing to change, and a second apply is a no-op.
        var again = await DiffAsync(withPre: true);
        Assert.True(again.Success, string.Join("; ", again.Errors.Select(e => e.Text)));
        Assert.Empty(again.Plan!.PreScripts);
        Assert.DoesNotContain(again.Plan.Actions, a => a.ObjectType == "Table");
        var second = await ApplyOperation.RunAsync(_provider, _ledger,
            new ApplyOperation.Request(_schemaDir, _url, PreDir: _preDir, ExpectedPlanHash: PlanFingerprint.Compute(again.Plan)));
        Assert.True(second.Success, string.Join("; ", second.Errors.Select(e => e.Text)));
        Assert.Empty(second.PreScripts?.Applied ?? []);
    }

    /// <summary>
    /// A hash reviewed without the pre-stage does not gate an apply that runs it — fail closed, and the
    /// rename the session already ran is rolled back with everything else.
    /// </summary>
    [SkippableFact]
    public async Task A_hash_from_a_diff_without_the_pre_stage_is_refused_and_nothing_is_kept()
    {
        var without = await DiffAsync(withPre: false);
        Assert.True(without.Success, string.Join("; ", without.Errors.Select(e => e.Text)));

        var outcome = await ApplyOperation.RunAsync(_provider, _ledger,
            new ApplyOperation.Request(_schemaDir, _url, PreDir: _preDir,
                ExpectedPlanHash: PlanFingerprint.Compute(without.Plan!)));

        Assert.Equal(ApplyOperation.FailureStage.PlanMismatch, outcome.Stage);
        Assert.Equal(ApplyOperation.Durability.RolledBack, outcome.Durability);
        Assert.True(await ExistsAsync("Assets"));
        Assert.False(await ExistsAsync("Equipment"));
        Assert.Empty(await _ledger.ReadAsync(_url, VersionedStage.Pre.LedgerKind));
    }

    [SkippableFact]
    public async Task A_failing_pre_stage_script_stops_diff_and_apply_before_anything_else()
    {
        await File.WriteAllTextAsync(Path.Combine(_preDir, "V2__broken.sql"), "ALTER TABLE \"NoSuchTable\" RENAME TO \"X\";");

        var diff = await DiffAsync(withPre: true);
        Assert.False(diff.Success);
        Assert.Contains(diff.Errors, e => e.Code == "pre_stage_failed" && e.Text.Contains("V2__broken.sql"));
        Assert.True(await ExistsAsync("Assets"));

        var outcome = await ApplyOperation.RunAsync(_provider, _ledger,
            new ApplyOperation.Request(_schemaDir, _url, PreDir: _preDir));
        Assert.Equal(ApplyOperation.FailureStage.PreStage, outcome.Stage);
        Assert.Equal(ApplyOperation.Durability.RolledBack, outcome.Durability);
        Assert.Equal(1, outcome.Attempted.Pre);       // V1 ran, then V2 failed
        Assert.Equal(0, outcome.Committed.Pre);       // and the session took it back
        Assert.True(await ExistsAsync("Assets"));
    }

    /// <summary>
    /// The view-redefinition analysis reads the live view through the same simulation. The pre-stage
    /// renames the view's column; read as of before it, the live column list differs from the file's
    /// and the view would be planned as DROP+CREATE — read as of after it, the two agree.
    /// </summary>
    [SkippableFact]
    public async Task View_analysis_sees_the_pre_stage_too()
    {
        await File.WriteAllTextAsync(Path.Combine(_preDir, "V2__rename_view_column.sql"),
            "ALTER VIEW \"AssetNames\" RENAME COLUMN \"Name\" TO \"Label\";");
        await File.WriteAllTextAsync(Path.Combine(_schemaDir, "views", "AssetNames.sql"), $"""
            CREATE VIEW "{_live.Name}"."AssetNames" AS SELECT "Name" AS "Label" FROM "{_live.Name}"."Equipment";
            """);

        var diff = await DiffAsync(withPre: true);

        Assert.True(diff.Success, string.Join("; ", diff.Errors.Select(e => e.Text)));
        var view = Assert.Single(diff.Plan!.Actions, a => a.ObjectName.EndsWith("AssetNames"));
        Assert.DoesNotContain("DROP VIEW", view.Sql ?? "", StringComparison.OrdinalIgnoreCase);
    }
}

