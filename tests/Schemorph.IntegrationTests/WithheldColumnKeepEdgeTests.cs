using Schemorph.Core.Operations;
using Schemorph.Core.Planning;
using Schemorph.Provider.SqlServer;
using Xunit.Abstractions;

namespace Schemorph.IntegrationTests;

/// <summary>
/// The shapes around a withheld column drop that are easy to get wrong: several columns of one table at
/// once, a computed column that reads the column, and the keys and indexes that include it. Each is
/// measured against a real engine, because what the comparison keeps is decided by what the engine reads
/// back, not by what the code expects it to.
/// </summary>
public sealed class WithheldColumnKeepEdgeTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly TestDatabase _db = new();
    private readonly string _dir = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), $"schemorph-keep-edge-{Guid.NewGuid():N}")).FullName;
    private readonly SqlServerProvider _provider = new();
    private readonly SqlServerLedgerStore _ledger = new();

    public WithheldColumnKeepEdgeTests(ITestOutputHelper output) => _output = output;

    private string SchemaDir => Path.Combine(_dir, "schema");

    private void Write(string table, string body, string trailing = "")
    {
        var path = Path.Combine(SchemaDir, "tables", $"dbo.{table}.sql");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, $"CREATE TABLE dbo.[{table}] (\n{body}\n);\nGO\n\n{trailing}");
    }

    private const string PartsOfItems = """
            Id INT NOT NULL PRIMARY KEY,
            ItemId INT NOT NULL CONSTRAINT FK_Parts_Items REFERENCES dbo.Items (Id)
        """;

    private async Task Create()
    {
        var created = await ApplyOperation.RunAsync(_provider, _ledger, new ApplyOperation.Request(SchemaDir, _db.Url));
        Assert.True(created.Success, string.Join("; ", created.Errors.Select(e => e.Text)));
    }

    private async Task<Plan> DiffKeeping()
    {
        var diff = await DiffOperation.RunAsync(_provider, _ledger, SchemaDir, _db.Url, allowDestructive: false);
        Assert.True(diff.Success, string.Join("; ", diff.Errors.Select(e => e.Text)));
        foreach (var a in diff.Plan!.Actions) _output.WriteLine($"action  {a.ObjectName} {a.Risk}");
        foreach (var e in diff.Plan.Excluded) _output.WriteLine($"excluded {e.ObjectName}: {e.Reason} | {e.Statements}");
        foreach (var m in diff.Plan.Messages) _output.WriteLine($"message {m.Code} {m.ObjectName}: {m.Text}");
        return diff.Plan;
    }

    private async Task ApplyKeeping(Plan plan)
    {
        var outcome = await ApplyOperation.RunAsync(_provider, _ledger,
            new ApplyOperation.Request(SchemaDir, _db.Url, ExpectedPlanHash: PlanFingerprint.Compute(plan)));
        Assert.True(outcome.Success, string.Join("; ", outcome.Errors.Select(e => e.Text)));
    }

    private int Count(string sql) => _db.Scalar<int>(sql);

    private int ColumnCount(string table, string column) =>
        Count($"SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID('dbo.{table}') AND name = '{column}'");

    private static void AssertKeptAndDependentRan(Plan plan)
    {
        Assert.Equal("dbo.Parts", Assert.Single(plan.Actions).ObjectName);
        Assert.DoesNotContain(plan.Messages, m => m.Code is "SCHEMORPH015" or "SCHEMORPH017");
    }

    [SkippableFact]
    public async Task Two_columns_of_one_table_are_both_kept_with_their_defaults()
    {
        Write("Items", """
                Id INT NOT NULL PRIMARY KEY,
                Name NVARCHAR(50) NOT NULL,
                Tag NVARCHAR(10) NOT NULL CONSTRAINT DF_Items_Tag DEFAULT N'none',
                Weight INT NOT NULL CONSTRAINT DF_Items_Weight DEFAULT 7
            """);
        await Create();
        _db.Execute("INSERT INTO dbo.Items (Id, Name, Tag, Weight) VALUES (1, N'alpha', N'kept', 3);");

        Write("Items", """
                Id INT NOT NULL PRIMARY KEY,
                Name NVARCHAR(50) NOT NULL
            """);
        Write("Parts", PartsOfItems);

        var plan = await DiffKeeping();
        AssertKeptAndDependentRan(plan);
        var held = Assert.Single(plan.Excluded, e => e.ObjectName == "dbo.Items");
        Assert.Contains("[Tag]", held.Statements);
        Assert.Contains("[Weight]", held.Statements);
        await ApplyKeeping(plan);

        Assert.Equal("kept", _db.Scalar<string>("SELECT Tag FROM dbo.Items WHERE Id = 1"));
        Assert.Equal(3, _db.Scalar<int>("SELECT Weight FROM dbo.Items WHERE Id = 1"));
        _db.Execute("INSERT INTO dbo.Items (Id, Name) VALUES (2, N'beta');");
        Assert.Equal("none", _db.Scalar<string>("SELECT Tag FROM dbo.Items WHERE Id = 2"));
        Assert.Equal(7, _db.Scalar<int>("SELECT Weight FROM dbo.Items WHERE Id = 2"));
        Assert.Equal(1, Count("SELECT COUNT(*) FROM sys.foreign_keys WHERE name = 'FK_Parts_Items'"));
    }

    [SkippableFact]
    public async Task A_computed_column_is_kept_with_the_column_it_reads()
    {
        Write("Items", """
                Id INT NOT NULL PRIMARY KEY,
                Qty INT NOT NULL,
                Price DECIMAL(10, 2) NOT NULL,
                Total AS (Price * Qty)
            """);
        await Create();
        _db.Execute("INSERT INTO dbo.Items (Id, Qty, Price) VALUES (1, 4, 2.50);");

        Write("Items", "Id INT NOT NULL PRIMARY KEY,\nQty INT NOT NULL");
        Write("Parts", PartsOfItems);

        var plan = await DiffKeeping();
        AssertKeptAndDependentRan(plan);
        await ApplyKeeping(plan);

        Assert.Equal(1, ColumnCount("Items", "Price"));
        Assert.Equal(1, ColumnCount("Items", "Total"));
        Assert.Equal(10.00m, _db.Scalar<decimal>("SELECT Total FROM dbo.Items WHERE Id = 1"));
    }

    [SkippableFact]
    public async Task A_computed_column_dropped_alone_stays_computed()
    {
        Write("Items", """
                Id INT NOT NULL PRIMARY KEY,
                Qty INT NOT NULL,
                Price DECIMAL(10, 2) NOT NULL,
                Total AS (Price * Qty)
            """);
        await Create();
        _db.Execute("INSERT INTO dbo.Items (Id, Qty, Price) VALUES (1, 4, 2.50);");

        Write("Items", """
                Id INT NOT NULL PRIMARY KEY,
                Qty INT NOT NULL,
                Price DECIMAL(10, 2) NOT NULL
            """);
        Write("Parts", PartsOfItems);

        var plan = await DiffKeeping();
        AssertKeptAndDependentRan(plan);
        await ApplyKeeping(plan);

        Assert.Equal(10.00m, _db.Scalar<decimal>("SELECT Total FROM dbo.Items WHERE Id = 1"));
        Assert.Equal(1, Count("SELECT COUNT(*) FROM sys.computed_columns WHERE object_id = OBJECT_ID('dbo.Items') AND name = 'Total'"));
    }

    [SkippableFact]
    public async Task A_unique_constraint_and_an_index_on_the_withheld_column_stay_with_it()
    {
        Write("Items", """
                Id INT NOT NULL PRIMARY KEY,
                Name NVARCHAR(50) NOT NULL,
                Tag NVARCHAR(10) NOT NULL CONSTRAINT UQ_Items_Tag UNIQUE
            """, "CREATE INDEX IX_Items_Name_Tag ON dbo.Items (Name, Tag);\nGO\n");
        await Create();
        _db.Execute("INSERT INTO dbo.Items (Id, Name, Tag) VALUES (1, N'alpha', N'one');");

        Write("Items", """
                Id INT NOT NULL PRIMARY KEY,
                Name NVARCHAR(50) NOT NULL
            """);
        Write("Parts", PartsOfItems);

        var plan = await DiffKeeping();
        AssertKeptAndDependentRan(plan);
        await ApplyKeeping(plan);

        Assert.Equal(1, ColumnCount("Items", "Tag"));
        Assert.Equal(1, Count("SELECT COUNT(*) FROM sys.indexes WHERE object_id = OBJECT_ID('dbo.Items') AND name = 'UQ_Items_Tag'"));
        Assert.Equal(1, Count("SELECT COUNT(*) FROM sys.indexes WHERE object_id = OBJECT_ID('dbo.Items') AND name = 'IX_Items_Name_Tag'"));
    }

    [SkippableFact]
    public async Task A_foreign_key_on_the_withheld_column_stays_while_its_target_is_still_declared()
    {
        Write("Cats", "Id INT NOT NULL PRIMARY KEY");
        Write("Items", """
                Id INT NOT NULL PRIMARY KEY,
                CatId INT NOT NULL CONSTRAINT FK_Items_Cats REFERENCES dbo.Cats (Id)
            """);
        await Create();
        _db.Execute("INSERT INTO dbo.Cats (Id) VALUES (5); INSERT INTO dbo.Items (Id, CatId) VALUES (1, 5);");

        Write("Items", "Id INT NOT NULL PRIMARY KEY");
        Write("Parts", PartsOfItems);

        var plan = await DiffKeeping();
        AssertKeptAndDependentRan(plan);
        await ApplyKeeping(plan);

        Assert.Equal(1, ColumnCount("Items", "CatId"));
        Assert.Equal(1, Count("SELECT COUNT(*) FROM sys.foreign_keys WHERE name = 'FK_Items_Cats'"));
    }

    /// <summary>
    /// A column that is part of the table's primary key. The key the desired state declares is narrower, so
    /// the table is rebuilt to carry it - and the rebuild carries the withheld column and its values along,
    /// because the comparison still declares the column. The drop stays reported as withheld.
    /// </summary>
    [SkippableFact]
    public async Task A_primary_key_that_loses_the_withheld_column_narrows_while_the_column_and_its_values_stay()
    {
        Write("Items", """
                Id INT NOT NULL,
                Tag NVARCHAR(10) NOT NULL,
                Name NVARCHAR(50) NOT NULL,
                CONSTRAINT PK_Items PRIMARY KEY (Id, Tag)
            """);
        await Create();
        _db.Execute("INSERT INTO dbo.Items (Id, Tag, Name) VALUES (1, N'a', N'alpha');");

        Write("Items", """
                Id INT NOT NULL,
                Name NVARCHAR(50) NOT NULL,
                CONSTRAINT PK_Items PRIMARY KEY (Id)
            """);
        Write("Parts", """
                Id INT NOT NULL PRIMARY KEY,
                ItemId INT NOT NULL
            """);

        var plan = await DiffKeeping();
        Assert.Contains(plan.Actions, a => a.ObjectName == "dbo.Items");
        Assert.Contains(plan.Actions, a => a.ObjectName == "dbo.Parts");
        Assert.DoesNotContain(plan.Messages, m => m.Code is "SCHEMORPH015" or "SCHEMORPH017");
        Assert.Contains(plan.Messages, m => m.Code == "SCHEMORPH102" && m.ObjectName == "dbo.Items");
        var held = Assert.Single(plan.Excluded, e => e.ObjectName == "dbo.Items" && e.Statements is not null);
        Assert.Contains("[Tag]", held.Statements);
        await ApplyKeeping(plan);

        Assert.Equal("a", _db.Scalar<string>("SELECT Tag FROM dbo.Items WHERE Id = 1"));
        Assert.Equal("alpha", _db.Scalar<string>("SELECT Name FROM dbo.Items WHERE Id = 1"));
        Assert.Equal(1, Count("""
            SELECT COUNT(*) FROM sys.index_columns ic
            JOIN sys.key_constraints k ON k.parent_object_id = ic.object_id AND k.unique_index_id = ic.index_id
            WHERE k.name = 'PK_Items' AND ic.object_id = OBJECT_ID('dbo.Items')
            """));
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }
}
