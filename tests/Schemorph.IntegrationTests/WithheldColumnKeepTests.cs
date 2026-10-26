using Schemorph.Core.Operations;
using Schemorph.Core.Planning;
using Schemorph.Provider.SqlServer;

namespace Schemorph.IntegrationTests;

/// <summary>
/// A dropped column the plan withholds is kept in the comparison with everything that hangs on it, so
/// the engine never writes the drop and the rest of the desired state runs. What hangs on the column is
/// part of keeping it: without its default a NOT NULL column starts failing every insert from code that
/// no longer knows about it, and nothing in the plan would have said so.
/// </summary>
public sealed class WithheldColumnKeepTests : IDisposable
{
    private readonly TestDatabase _db = new();
    private readonly string _dir = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), $"schemorph-keep-{Guid.NewGuid():N}")).FullName;
    private readonly SqlServerProvider _provider = new();
    private readonly SqlServerLedgerStore _ledger = new();

    private string SchemaDir => Path.Combine(_dir, "schema");

    private void Write(string table, string body)
    {
        var path = Path.Combine(SchemaDir, "tables", $"dbo.{table}.sql");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, $"CREATE TABLE dbo.[{table}] (\n{body}\n);\nGO\n\n");
    }

    private const string ItemsWithTag = """
            Id INT NOT NULL PRIMARY KEY,
            Name NVARCHAR(50) NOT NULL,
            Tag NVARCHAR(10) NOT NULL CONSTRAINT DF_Items_Tag DEFAULT N'none'
        """;

    private const string ItemsWithoutTag = """
            Id INT NOT NULL PRIMARY KEY,
            Name NVARCHAR(50) NOT NULL
        """;

    private const string PartsOfItems = """
            Id INT NOT NULL PRIMARY KEY,
            ItemId INT NOT NULL CONSTRAINT FK_Parts_Items REFERENCES dbo.Items (Id)
        """;

    private async Task SeedItems()
    {
        Write("Items", ItemsWithTag);
        var created = await ApplyOperation.RunAsync(_provider, _ledger, new ApplyOperation.Request(SchemaDir, _db.Url));
        Assert.True(created.Success, string.Join("; ", created.Errors.Select(e => e.Text)));
        _db.Execute("INSERT INTO dbo.Items (Id, Name, Tag) VALUES (1, N'alpha', N'kept');");
    }

    private int Count(string sql) => _db.Scalar<int>(sql);

    [SkippableFact]
    public async Task A_withheld_column_keeps_its_default_so_inserts_that_do_not_name_it_still_work()
    {
        await SeedItems();
        Write("Items", ItemsWithoutTag);
        Write("Parts", PartsOfItems);

        var diff = await DiffOperation.RunAsync(_provider, _ledger, SchemaDir, _db.Url, allowDestructive: false);
        Assert.True(diff.Success, string.Join("; ", diff.Errors.Select(e => e.Text)));
        Assert.Equal("dbo.Parts", Assert.Single(diff.Plan!.Actions).ObjectName);
        Assert.DoesNotContain(diff.Plan.Messages, m => m.Code is "SCHEMORPH015" or "SCHEMORPH017");

        var outcome = await ApplyOperation.RunAsync(_provider, _ledger,
            new ApplyOperation.Request(SchemaDir, _db.Url, ExpectedPlanHash: PlanFingerprint.Compute(diff.Plan)));
        Assert.True(outcome.Success, string.Join("; ", outcome.Errors.Select(e => e.Text)));

        // The column, its values and its default are as they were; the new table and its key exist.
        Assert.Equal("kept", _db.Scalar<string>("SELECT Tag FROM dbo.Items WHERE Id = 1"));
        Assert.Equal(1, Count("SELECT COUNT(*) FROM sys.default_constraints WHERE name = 'DF_Items_Tag'"));
        _db.Execute("INSERT INTO dbo.Items (Id, Name) VALUES (2, N'beta');");
        Assert.Equal("none", _db.Scalar<string>("SELECT Tag FROM dbo.Items WHERE Id = 2"));
        Assert.Equal(1, Count("SELECT COUNT(*) FROM sys.foreign_keys WHERE name = 'FK_Parts_Items'"));
    }

    [SkippableFact]
    public async Task Enabling_destructive_changes_afterwards_drops_the_column_and_its_default_together()
    {
        await SeedItems();
        Write("Items", ItemsWithoutTag);

        var withheld = await ApplyOperation.RunAsync(_provider, _ledger, new ApplyOperation.Request(SchemaDir, _db.Url));
        Assert.True(withheld.Success, string.Join("; ", withheld.Errors.Select(e => e.Text)));
        Assert.Equal(1, Count("SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID('dbo.Items') AND name = 'Tag'"));

        var diff = await DiffOperation.RunAsync(_provider, _ledger, SchemaDir, _db.Url, allowDestructive: true);
        Assert.True(diff.Success, string.Join("; ", diff.Errors.Select(e => e.Text)));
        Assert.Equal(RiskLevel.Destructive, Assert.Single(diff.Plan!.Actions).Risk);
        Assert.DoesNotContain(diff.Plan.Excluded, e => e.Statements is not null);

        var outcome = await ApplyOperation.RunAsync(_provider, _ledger,
            new ApplyOperation.Request(SchemaDir, _db.Url, AllowDestructive: true,
                ExpectedPlanHash: PlanFingerprint.Compute(diff.Plan)));
        Assert.True(outcome.Success, string.Join("; ", outcome.Errors.Select(e => e.Text)));

        Assert.Equal(0, Count("SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID('dbo.Items') AND name = 'Tag'"));
        Assert.Equal(0, Count("SELECT COUNT(*) FROM sys.default_constraints WHERE name = 'DF_Items_Tag'"));
        Assert.Equal(1, Count("SELECT COUNT(*) FROM dbo.Items"));
    }

    /// <summary>
    /// A column that cannot be put back faithfully is not kept: here its foreign key points at a table
    /// the desired state no longer declares, so a model with the column back does not validate. The
    /// change to the table is then withheld whole, with what depends on it, and the plan says why - the
    /// answer when nothing better can be proven, and never a drop the plan did not mention.
    /// </summary>
    [SkippableFact]
    public async Task A_column_that_cannot_be_put_back_withholds_the_table_whole_and_says_so()
    {
        Write("Cats", "Id INT NOT NULL PRIMARY KEY");
        Write("Items", """
                Id INT NOT NULL PRIMARY KEY,
                CatId INT NOT NULL CONSTRAINT FK_Items_Cats REFERENCES dbo.Cats (Id)
            """);
        var created = await ApplyOperation.RunAsync(_provider, _ledger, new ApplyOperation.Request(SchemaDir, _db.Url));
        Assert.True(created.Success, string.Join("; ", created.Errors.Select(e => e.Text)));

        File.Delete(Path.Combine(SchemaDir, "tables", "dbo.Cats.sql"));
        Write("Items", "Id INT NOT NULL PRIMARY KEY");
        Write("Parts", PartsOfItems);

        var diff = await DiffOperation.RunAsync(_provider, _ledger, SchemaDir, _db.Url, allowDestructive: false);
        Assert.True(diff.Success, string.Join("; ", diff.Errors.Select(e => e.Text)));

        Assert.Empty(diff.Plan!.Actions);
        Assert.Contains(diff.Plan.Messages, m => m.Code == "SCHEMORPH017");
        Assert.Equal("dbo.Parts", Assert.Single(diff.Plan.Messages, m => m.Code == "SCHEMORPH015").ObjectName);
        Assert.Contains(diff.Plan.Excluded, e => e.ObjectName == "dbo.Items");

        var outcome = await ApplyOperation.RunAsync(_provider, _ledger,
            new ApplyOperation.Request(SchemaDir, _db.Url, ExpectedPlanHash: PlanFingerprint.Compute(diff.Plan)));
        Assert.True(outcome.Success, string.Join("; ", outcome.Errors.Select(e => e.Text)));
        Assert.Empty(outcome.Applied);
        Assert.Equal(1, Count("SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID('dbo.Items') AND name = 'CatId'"));
        Assert.Equal(0, Count("SELECT COUNT(*) FROM sys.tables WHERE name = 'Parts'"));
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }
}
