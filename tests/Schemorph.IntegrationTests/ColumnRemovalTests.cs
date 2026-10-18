using Schemorph.Core.Operations;
using Schemorph.Core.Planning;
using Schemorph.Provider.SqlServer;

namespace Schemorph.IntegrationTests;

/// <summary>
/// A column removed and not put back. The rename tests already reach the criterion
/// in <c>docs/design-principles.md</c> §4, but they reach it through a drop that
/// arrives beside an add — so the attribution always has a second child difference
/// in the same table to look at. Here the removal stands alone, which is the plainer
/// shape and the one a desired-state edit actually produces.
///
/// The last test changes nothing but the table's name. An object name travels from
/// the comparison tree to the generated script through two different spellings, and
/// a name the engine has to bracket is where those two could part company. That
/// parting would be silent: a classifier that fails to recognise a removal reports
/// the same thing as one that correctly judged there was none — an ordinary alter,
/// which a plain apply then carries out.
/// </summary>
public sealed class ColumnRemovalTests : IDisposable
{
    private readonly TestDatabase _db = new();
    private readonly string _dir = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), $"schemorph-removal-{Guid.NewGuid():N}")).FullName;
    private readonly SqlServerProvider _provider = new();
    private readonly SqlServerLedgerStore _ledger = new();

    private string SchemaDir => Path.Combine(_dir, "schema");

    private void Write(string table, string columns)
    {
        var path = Path.Combine(SchemaDir, "tables", $"dbo.{table}.sql");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, $"CREATE TABLE dbo.[{table}] (\n{columns}\n);\nGO\n\n");
    }

    private void WriteWithNotes(string table) => Write(table, """
            Id INT NOT NULL PRIMARY KEY,
            Name NVARCHAR(50) NOT NULL,
            Notes NVARCHAR(100) NULL
        """);

    private void WriteWithoutNotes(string table) => Write(table, """
            Id INT NOT NULL PRIMARY KEY,
            Name NVARCHAR(50) NOT NULL
        """);

    /// <summary>The live state every test starts from: one row with a value worth keeping.</summary>
    private async Task Seed(string table)
    {
        WriteWithNotes(table);
        var created = await ApplyOperation.RunAsync(_provider, _ledger,
            new ApplyOperation.Request(SchemaDir, _db.Url));
        Assert.True(created.Success, string.Join("; ", created.Errors.Select(e => e.Text)));
        _db.Execute($"INSERT INTO dbo.[{table}] (Id, Name, Notes) VALUES (1, N'alpha', N'keep me');");
    }

    private int Columns(string table, string column) => _db.Scalar<int>(
        $"SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID('dbo.[{table}]') AND name = '{column}'");

    [SkippableFact]
    public async Task Removing_a_column_outright_is_withheld_without_the_flag()
    {
        await Seed("Workspaces");
        WriteWithoutNotes("Workspaces");

        var diff = await DiffOperation.RunAsync(_provider, _ledger, SchemaDir, _db.Url, allowDestructive: false);
        Assert.True(diff.Success, string.Join("; ", diff.Errors.Select(e => e.Text)));

        // The generator words it as an alter that drops a column, and the criterion
        // is the loss rather than the syntax — so the gate has to see it.
        Assert.Contains("DROP COLUMN", diff.UpdateScript!, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(diff.Plan!.Actions);
        Assert.Contains(diff.Plan.Messages, m => m.Code == "SCHEMORPH001");
        Assert.Contains(diff.Plan.Excluded, e => e.ObjectName == "dbo.Workspaces");

        // And the flag reads false here, because withholding the change is what
        // emptied the list it counts. Pinned rather than assumed: a caller that
        // stops on hasDestructiveChanges alone does not stop on this plan, which
        // is the one case it most needs to. SCHEMORPH001 and excluded[] are the
        // signal; the flag answers "is a destructive change still in the plan".
        Assert.False(diff.Plan.HasDestructiveChanges);

        var outcome = await ApplyOperation.RunAsync(_provider, _ledger,
            new ApplyOperation.Request(SchemaDir, _db.Url));
        Assert.True(outcome.Success, string.Join("; ", outcome.Errors.Select(e => e.Text)));
        Assert.Empty(outcome.Applied);

        Assert.Equal(1, Columns("Workspaces", "Notes"));
        Assert.Equal("keep me", _db.Scalar<string>("SELECT TOP 1 Notes FROM dbo.Workspaces"));
    }

    [SkippableFact]
    public async Task Allowing_the_removal_takes_the_column_and_its_values()
    {
        await Seed("Workspaces");
        WriteWithoutNotes("Workspaces");

        var diff = await DiffOperation.RunAsync(_provider, _ledger, SchemaDir, _db.Url, allowDestructive: true);
        Assert.True(diff.Success, string.Join("; ", diff.Errors.Select(e => e.Text)));

        var action = Assert.Single(diff.Plan!.Actions);
        Assert.Equal(RiskLevel.Destructive, action.Risk);
        Assert.True(diff.Plan.HasDestructiveChanges);

        var outcome = await ApplyOperation.RunAsync(_provider, _ledger,
            new ApplyOperation.Request(SchemaDir, _db.Url, AllowDestructive: true,
                ExpectedPlanHash: PlanFingerprint.Compute(diff.Plan)));
        Assert.True(outcome.Success, string.Join("; ", outcome.Errors.Select(e => e.Text)));

        Assert.Equal(0, Columns("Workspaces", "Notes"));
        Assert.Equal(1, _db.Scalar<int>("SELECT COUNT(*) FROM dbo.Workspaces"));
    }

    /// <summary>
    /// The same removal under a name the engine has to bracket. Nothing about the
    /// loss changed, so nothing about the classification may.
    /// </summary>
    [SkippableFact]
    public async Task A_reserved_word_table_name_does_not_hide_the_removal()
    {
        await Seed("Order");
        WriteWithoutNotes("Order");

        var diff = await DiffOperation.RunAsync(_provider, _ledger, SchemaDir, _db.Url, allowDestructive: false);
        Assert.True(diff.Success, string.Join("; ", diff.Errors.Select(e => e.Text)));

        Assert.Contains("DROP COLUMN", diff.UpdateScript!, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(diff.Plan!.Actions);
        Assert.Contains(diff.Plan.Messages, m => m.Code == "SCHEMORPH001");
        Assert.Contains(diff.Plan.Excluded, e => e.ObjectName == "dbo.Order");

        var outcome = await ApplyOperation.RunAsync(_provider, _ledger,
            new ApplyOperation.Request(SchemaDir, _db.Url));
        Assert.True(outcome.Success, string.Join("; ", outcome.Errors.Select(e => e.Text)));
        Assert.Empty(outcome.Applied);

        Assert.Equal(1, Columns("Order", "Notes"));
        Assert.Equal("keep me", _db.Scalar<string>("SELECT TOP 1 Notes FROM dbo.[Order]"));
    }

    /// <summary>
    /// The engine withholds a change only together with everything that depends on it. A
    /// foreign key added in the same plan depends on the table it references, so while it
    /// runs the engine cannot leave that table's change out — the column drop gated out of
    /// the plan would run anyway, with the plan, the apply result and the ledger all saying
    /// it did not. The dependent change is withheld with it instead, and says why.
    /// </summary>
    [SkippableFact]
    public async Task A_gated_removal_stays_withheld_when_a_new_foreign_key_references_its_table()
    {
        await Seed("Products");
        WriteWithoutNotes("Products");
        Write("Parts", """
                Id INT NOT NULL PRIMARY KEY,
                ProductId INT NOT NULL CONSTRAINT FK_Parts_Products REFERENCES dbo.Products (Id)
            """);
        // A second table whose own removal nothing depends on: withheld exactly as before.
        Write("Notes", """
                Id INT NOT NULL PRIMARY KEY
            """);
        _db.Execute("CREATE TABLE dbo.Notes (Id INT NOT NULL PRIMARY KEY, Body NVARCHAR(20) NULL);");

        var diff = await DiffOperation.RunAsync(_provider, _ledger, SchemaDir, _db.Url, allowDestructive: false);
        Assert.True(diff.Success, string.Join("; ", diff.Errors.Select(e => e.Text)));

        Assert.Empty(diff.Plan!.Actions);
        Assert.Contains(diff.Plan.Excluded, e => e.ObjectName == "dbo.Products");
        Assert.Contains(diff.Plan.Excluded, e => e.ObjectName == "dbo.Notes");
        var parts = Assert.Single(diff.Plan.Excluded, e => e.ObjectName == "dbo.Parts");
        Assert.Contains("dbo.Products", parts.Reason);
        var withheld = Assert.Single(diff.Plan.Messages, m => m.Code == "SCHEMORPH015");
        Assert.Equal("dbo.Parts", withheld.ObjectName);

        var outcome = await ApplyOperation.RunAsync(_provider, _ledger,
            new ApplyOperation.Request(SchemaDir, _db.Url, ExpectedPlanHash: PlanFingerprint.Compute(diff.Plan)));
        Assert.True(outcome.Success, string.Join("; ", outcome.Errors.Select(e => e.Text)));
        Assert.Empty(outcome.Applied);

        Assert.Equal(1, Columns("Products", "Notes"));
        Assert.Equal("keep me", _db.Scalar<string>("SELECT TOP 1 Notes FROM dbo.Products"));
        Assert.Equal(1, Columns("Notes", "Body"));
        Assert.Equal(0, _db.Scalar<int>("SELECT COUNT(*) FROM sys.tables WHERE name = 'Parts'"));
    }

    /// <summary>
    /// The same coupling where the engine cannot say what depends on the table: a view that reads
    /// it and does not change makes the engine's dependency query fail. The plan still has to know
    /// the new foreign key depends on the table, so it reads that from the key's own definition.
    /// </summary>
    [SkippableFact]
    public async Task The_coupling_is_found_when_an_unchanged_view_reads_the_table_too()
    {
        await Seed("Products");
        _db.Execute("CREATE VIEW dbo.ProductNames AS SELECT p.Id, p.Name FROM dbo.Products AS p");
        WriteWithoutNotes("Products");
        var view = Path.Combine(SchemaDir, "views", "dbo.ProductNames.sql");
        Directory.CreateDirectory(Path.GetDirectoryName(view)!);
        File.WriteAllText(view, "CREATE VIEW dbo.ProductNames AS SELECT p.Id, p.Name FROM dbo.Products AS p\nGO\n");
        Write("Parts", """
                Id INT NOT NULL PRIMARY KEY,
                ProductId INT NOT NULL CONSTRAINT FK_Parts_Products REFERENCES dbo.Products (Id)
            """);

        var diff = await DiffOperation.RunAsync(_provider, _ledger, SchemaDir, _db.Url, allowDestructive: false);
        Assert.True(diff.Success, string.Join("; ", diff.Errors.Select(e => e.Text)));
        Assert.DoesNotContain(diff.Plan!.Actions, a => a.ObjectName == "dbo.Parts");
        Assert.Equal("dbo.Parts", Assert.Single(diff.Plan.Messages, m => m.Code == "SCHEMORPH015").ObjectName);

        var outcome = await ApplyOperation.RunAsync(_provider, _ledger,
            new ApplyOperation.Request(SchemaDir, _db.Url, ExpectedPlanHash: PlanFingerprint.Compute(diff.Plan)));
        Assert.True(outcome.Success, string.Join("; ", outcome.Errors.Select(e => e.Text)));
        Assert.Equal(1, Columns("Products", "Notes"));
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }
}
