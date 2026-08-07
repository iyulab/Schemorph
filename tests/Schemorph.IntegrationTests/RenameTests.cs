using Schemorph.Core.Operations;
using Schemorph.Core.Planning;
using Schemorph.Provider.SqlServer;

namespace Schemorph.IntegrationTests;

/// <summary>
/// The same limit as the PostgreSQL <c>RenameTests</c>, measured on this engine
/// because the two do not end in the same place. Objects are matched by name on
/// both, so a rename is a drop beside a create on both — but what the gate does
/// with the losing half is where they part, and a page that documents the hazard
/// has to be right about which engine refuses it.
///
/// Data is the evidence. A rename that "worked" leaves the row in place and the
/// column correctly named, so every check short of reading a value agrees with it.
/// </summary>
public sealed class RenameTests : IDisposable
{
    private readonly TestDatabase _db = new();
    private readonly string _dir = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), $"schemorph-rename-{Guid.NewGuid():N}")).FullName;
    private readonly SqlServerProvider _provider = new();
    private readonly SqlServerLedgerStore _ledger = new();

    private string SchemaDir => Path.Combine(_dir, "schema");

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

    /// <summary>The live state every test starts from: one row with a value worth keeping.</summary>
    private async Task SeedLiveWithNotes()
    {
        WriteDesired("Notes");
        var created = await ApplyOperation.RunAsync(_provider, _ledger,
            new ApplyOperation.Request(SchemaDir, _db.Url));
        Assert.True(created.Success, string.Join("; ", created.Errors.Select(e => e.Text)));
        _db.Execute("INSERT INTO dbo.Workspaces (Id, Name, Notes) VALUES (1, N'alpha', N'keep me');");
    }

    private int Columns(string name) => _db.Scalar<int>(
        $"SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID('dbo.Workspaces') AND name = '{name}'");

    [SkippableFact]
    public async Task A_renamed_column_reaches_the_plan_as_a_drop_beside_an_add()
    {
        await SeedLiveWithNotes();
        WriteDesired("Remarks");

        var diff = await DiffOperation.RunAsync(_provider, _ledger, SchemaDir, _db.Url, allowDestructive: false);
        Assert.True(diff.Success, string.Join("; ", diff.Errors.Select(e => e.Text)));

        // Two independent statements. The tool never had a rename to emit.
        Assert.Contains("DROP COLUMN", diff.UpdateScript!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ADD", diff.UpdateScript!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sp_rename", diff.UpdateScript!, StringComparison.OrdinalIgnoreCase);

        // Which the gate now sees on this engine too: the losing half is withheld
        // and the plan says so, rather than an ordinary alter carrying it out.
        Assert.Empty(diff.Plan!.Actions);
        Assert.Contains(diff.Plan.Messages, m => m.Code == "SCHEMORPH001");
        Assert.Contains(diff.Plan.Excluded, e => e.ObjectName == "dbo.Workspaces");

        var outcome = await ApplyOperation.RunAsync(_provider, _ledger,
            new ApplyOperation.Request(SchemaDir, _db.Url));
        Assert.True(outcome.Success, string.Join("; ", outcome.Errors.Select(e => e.Text)));
        Assert.Empty(outcome.Applied);

        Assert.Equal(1, Columns("Notes"));
        Assert.Equal(0, Columns("Remarks"));
        Assert.Equal("keep me", _db.Scalar<string>("SELECT TOP 1 Notes FROM dbo.Workspaces"));
    }

    /// <summary>
    /// Forced through, the shape arrives and the values do not — the same ending
    /// as the other engine, which is the point: one criterion, two providers.
    /// </summary>
    [SkippableFact]
    public async Task Allowing_the_destructive_change_moves_the_name_but_not_the_values()
    {
        await SeedLiveWithNotes();
        WriteDesired("Remarks");

        var diff = await DiffOperation.RunAsync(_provider, _ledger, SchemaDir, _db.Url, allowDestructive: true);
        Assert.True(diff.Success, string.Join("; ", diff.Errors.Select(e => e.Text)));

        var action = Assert.Single(diff.Plan!.Actions);
        Assert.Equal(RiskLevel.Destructive, action.Risk);
        Assert.True(diff.Plan.HasDestructiveChanges);
        Assert.Contains(diff.Plan.Messages, m => m.Code == "SCHEMORPH103");

        var outcome = await ApplyOperation.RunAsync(_provider, _ledger,
            new ApplyOperation.Request(SchemaDir, _db.Url, AllowDestructive: true,
                ExpectedPlanHash: PlanFingerprint.Compute(diff.Plan)));
        Assert.True(outcome.Success, string.Join("; ", outcome.Errors.Select(e => e.Text)));

        Assert.Equal(0, Columns("Notes"));
        Assert.Equal(1, Columns("Remarks"));
        Assert.Equal(1, _db.Scalar<int>("SELECT COUNT(*) FROM dbo.Workspaces"));
        Assert.Equal(1, _db.Scalar<int>("SELECT COUNT(*) FROM dbo.Workspaces WHERE Remarks IS NULL"));
    }

    /// <summary>
    /// The same limit one level up, where the per-object gate makes the result look
    /// stranger than a refusal: the create is safe on its own and applies, the drop
    /// is withheld, and what is left is an empty table beside the full one. Pinned
    /// on this engine too because the shape is the generator's, not the model's —
    /// a claim about what a publish leaves behind cannot be inherited across
    /// providers.
    /// </summary>
    [SkippableFact]
    public async Task A_renamed_table_becomes_a_create_beside_a_withheld_drop()
    {
        await SeedLiveWithNotes();

        File.Delete(Path.Combine(SchemaDir, "tables", "dbo.Workspaces.sql"));
        File.WriteAllText(Path.Combine(SchemaDir, "tables", "dbo.Workareas.sql"), """
            CREATE TABLE dbo.Workareas (
                Id INT NOT NULL PRIMARY KEY,
                Name NVARCHAR(50) NOT NULL,
                Notes NVARCHAR(100) NULL
            );
            GO

            """);

        var diff = await DiffOperation.RunAsync(_provider, _ledger, SchemaDir, _db.Url, allowDestructive: false);
        Assert.True(diff.Success, string.Join("; ", diff.Errors.Select(e => e.Text)));

        Assert.Contains(diff.Plan!.Excluded, e => e.ObjectName == "dbo.Workspaces");
        Assert.Contains(diff.Plan.Actions, a => a.ObjectName == "dbo.Workareas");

        var outcome = await ApplyOperation.RunAsync(_provider, _ledger,
            new ApplyOperation.Request(SchemaDir, _db.Url,
                ExpectedPlanHash: PlanFingerprint.Compute(diff.Plan)));
        Assert.True(outcome.Success, string.Join("; ", outcome.Errors.Select(e => e.Text)));

        // Both tables exist, and the rows stayed with the old name.
        Assert.Equal(1, _db.Scalar<int>("SELECT COUNT(*) FROM sys.tables WHERE name = 'Workspaces'"));
        Assert.Equal(1, _db.Scalar<int>("SELECT COUNT(*) FROM sys.tables WHERE name = 'Workareas'"));
        Assert.Equal(1, _db.Scalar<int>("SELECT COUNT(*) FROM dbo.Workspaces"));
        Assert.Equal(0, _db.Scalar<int>("SELECT COUNT(*) FROM dbo.Workareas"));
    }

    /// <summary>
    /// The other side of the criterion, and the one a gate gets wrong by being too
    /// eager: widening a column is not losing it. Nothing here is unrecoverable, so
    /// the change stays an ordinary alter and a plain apply carries it out — if this
    /// ever starts being gated, pipelines that were correct go red for no loss.
    /// </summary>
    [SkippableFact]
    public async Task Changing_a_column_definition_is_not_gated()
    {
        await SeedLiveWithNotes();

        var path = Path.Combine(SchemaDir, "tables", "dbo.Workspaces.sql");
        File.WriteAllText(path, """
            CREATE TABLE dbo.Workspaces (
                Id INT NOT NULL PRIMARY KEY,
                Name NVARCHAR(50) NOT NULL,
                Notes NVARCHAR(400) NULL
            );
            GO

            """);

        var diff = await DiffOperation.RunAsync(_provider, _ledger, SchemaDir, _db.Url, allowDestructive: false);
        Assert.True(diff.Success, string.Join("; ", diff.Errors.Select(e => e.Text)));

        var action = Assert.Single(diff.Plan!.Actions);
        Assert.NotEqual(RiskLevel.Destructive, action.Risk);
        Assert.False(diff.Plan.HasDestructiveChanges);
        Assert.DoesNotContain(diff.Plan.Messages, m => m.Code == "SCHEMORPH001");

        var outcome = await ApplyOperation.RunAsync(_provider, _ledger,
            new ApplyOperation.Request(SchemaDir, _db.Url,
                ExpectedPlanHash: PlanFingerprint.Compute(diff.Plan)));
        Assert.True(outcome.Success, string.Join("; ", outcome.Errors.Select(e => e.Text)));

        // Widened in place, and the value came along.
        Assert.Equal(400, _db.Scalar<int>(
            "SELECT CAST(max_length / 2 AS INT) FROM sys.columns " +
            "WHERE object_id = OBJECT_ID('dbo.Workspaces') AND name = 'Notes'"));
        Assert.Equal("keep me", _db.Scalar<string>("SELECT TOP 1 Notes FROM dbo.Workspaces"));
    }

    /// <summary>
    /// The way out is the same on both engines, and it is the reason the limitation
    /// is livable: rename with the engine's own statement first, then let the files
    /// catch up.
    /// </summary>
    [SkippableFact]
    public async Task Renaming_with_the_engine_reconciles_and_keeps_the_values()
    {
        await SeedLiveWithNotes();
        WriteDesired("Remarks");

        _db.Execute("EXEC sp_rename 'dbo.Workspaces.Notes', 'Remarks', 'COLUMN';");

        var diff = await DiffOperation.RunAsync(_provider, _ledger, SchemaDir, _db.Url, allowDestructive: false);
        Assert.True(diff.Success, string.Join("; ", diff.Errors.Select(e => e.Text)));

        // Converged: the desired state was already true before the tool looked.
        Assert.Empty(diff.Plan!.Actions);
        Assert.Equal("keep me", _db.Scalar<string>("SELECT TOP 1 Remarks FROM dbo.Workspaces"));
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }
}
