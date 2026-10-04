using Schemorph.Core.Migrations;
using Schemorph.Core.Operations;
using Schemorph.Core.Planning;
using Schemorph.Provider.SqlServer;

namespace Schemorph.IntegrationTests;

/// <summary>
/// Pre-stage scripts on SQL Server, where the comparison cannot be made to see them before they
/// commit: the engine compares on a connection of its own. So the work takes two runs. The first plan
/// is the pre-stage alone, gated like any plan; its apply runs the scripts — each committed with its
/// ledger row — and reports the rest deferred. The second run sees the rename done and plans what is
/// left. The column's value is the evidence that the rename, not a drop and an add, is what happened.
/// </summary>
public sealed class SqlServerPreStageTests : IDisposable
{
    private readonly TestDatabase _db = new();
    private readonly string _dir = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), $"schemorph-pre-{Guid.NewGuid():N}")).FullName;
    private readonly SqlServerProvider _provider = new();
    private readonly SqlServerLedgerStore _ledger = new();

    private string SchemaDir => Path.Combine(_dir, "schema");
    private string PreDir => Path.Combine(_dir, "pre");

    private void WriteDesired(string columnName)
    {
        var path = Path.Combine(SchemaDir, "tables", "dbo.Workspaces.sql");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, $"""
            CREATE TABLE dbo.Workspaces (
                Id INT NOT NULL PRIMARY KEY,
                Name NVARCHAR(50) NOT NULL,
                {columnName} NVARCHAR(100) NULL
            );
            GO

            """);
    }

    private async Task SeedAsync()
    {
        WriteDesired("Notes");
        var created = await ApplyOperation.RunAsync(_provider, _ledger, new ApplyOperation.Request(SchemaDir, _db.Url));
        Assert.True(created.Success, string.Join("; ", created.Errors.Select(e => e.Text)));
        _db.Execute("INSERT INTO dbo.Workspaces (Id, Name, Notes) VALUES (1, N'alpha', N'keep me');");

        // The desired state renames the column; the pre-stage says so.
        WriteDesired("Remarks");
        Directory.CreateDirectory(PreDir);
        File.WriteAllText(Path.Combine(PreDir, "V1__rename_notes.sql"),
            "EXEC sp_rename 'dbo.Workspaces.Notes', 'Remarks', 'COLUMN';");
    }

    private int Columns(string name) => _db.Scalar<int>(
        $"SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID('dbo.Workspaces') AND name = '{name}'");

    private Task<DiffOperation.DiffResult> DiffAsync() =>
        DiffOperation.RunAsync(_provider, _ledger, SchemaDir, _db.Url, allowDestructive: false, preDir: PreDir);

    [SkippableFact]
    public async Task The_first_run_is_the_pre_stage_alone_and_the_second_plans_the_rest()
    {
        await SeedAsync();

        // Run one: the plan is the pre-stage, nothing else — not the drop and add a comparison of today's
        // schema would show.
        var first = await DiffAsync();
        Assert.True(first.Success, string.Join("; ", first.Errors.Select(e => e.Text)));
        Assert.Empty(first.Plan!.Actions);
        Assert.Equal("V1__rename_notes.sql", Assert.Single(first.Plan.PreScripts).FileName);
        Assert.Contains(first.Plan.Messages, m => m.Code == "SCHEMORPH014");
        Assert.True(first.Plan.HasPendingWork);
        Assert.Equal(1, Columns("Notes"));   // diff ran nothing

        var applied = await ApplyOperation.RunAsync(_provider, _ledger,
            new ApplyOperation.Request(SchemaDir, _db.Url, PreDir: PreDir,
                ExpectedPlanHash: PlanFingerprint.Compute(first.Plan)));
        Assert.True(applied.Success, string.Join("; ", applied.Errors.Select(e => e.Text)));
        Assert.True(applied.Deferred);
        Assert.Equal(new[] { "V1__rename_notes.sql" }, applied.PreScripts!.Applied);
        Assert.Empty(applied.Applied);
        Assert.Equal("keep me", _db.Scalar<string>("SELECT TOP 1 Remarks FROM dbo.Workspaces"));
        Assert.Single(await _ledger.ReadAsync(_db.Url, VersionedStage.Pre.LedgerKind));

        // Run two: the rename is done and recorded, so nothing is pending and nothing is left to change.
        var second = await DiffAsync();
        Assert.True(second.Success, string.Join("; ", second.Errors.Select(e => e.Text)));
        Assert.Empty(second.Plan!.PreScripts);
        Assert.Empty(second.Plan.Actions);
        Assert.False(second.Plan.HasPendingWork);
        var converged = await ApplyOperation.RunAsync(_provider, _ledger,
            new ApplyOperation.Request(SchemaDir, _db.Url, PreDir: PreDir,
                ExpectedPlanHash: PlanFingerprint.Compute(second.Plan)));
        Assert.True(converged.Success, string.Join("; ", converged.Errors.Select(e => e.Text)));
        Assert.False(converged.Deferred);
        Assert.Equal("keep me", _db.Scalar<string>("SELECT TOP 1 Remarks FROM dbo.Workspaces"));
        Assert.Equal(0, Columns("Notes"));
    }

    [SkippableFact]
    public async Task A_hash_reviewed_without_the_pre_stage_runs_nothing()
    {
        await SeedAsync();
        var without = await DiffOperation.RunAsync(_provider, _ledger, SchemaDir, _db.Url, allowDestructive: false);
        Assert.True(without.Success, string.Join("; ", without.Errors.Select(e => e.Text)));

        var outcome = await ApplyOperation.RunAsync(_provider, _ledger,
            new ApplyOperation.Request(SchemaDir, _db.Url, PreDir: PreDir,
                ExpectedPlanHash: PlanFingerprint.Compute(without.Plan!)));

        Assert.Equal(ApplyOperation.FailureStage.PlanMismatch, outcome.Stage);
        Assert.Equal(1, Columns("Notes"));
        Assert.Empty(await _ledger.ReadAsync(_db.Url, VersionedStage.Pre.LedgerKind));
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }
}
