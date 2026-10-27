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

    private void WriteFile(string relative, string content)
    {
        var path = Path.Combine(SchemaDir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    [SkippableFact]
    public async Task A_filtered_index_that_reads_the_withheld_column_stays_with_it()
    {
        Write("Items", """
                Id INT NOT NULL PRIMARY KEY,
                Name NVARCHAR(50) NOT NULL,
                Tag NVARCHAR(10) NULL
            """, "CREATE INDEX IX_Items_Tagged ON dbo.Items (Name) WHERE Tag IS NOT NULL;\nGO\n");
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
        Assert.Equal(1, Count("SELECT COUNT(*) FROM sys.indexes WHERE object_id = OBJECT_ID('dbo.Items') AND name = 'IX_Items_Tagged'"));
    }

    [SkippableFact]
    public async Task A_schema_bound_view_dropped_with_the_withheld_column_goes_while_the_column_stays()
    {
        Write("Items", """
                Id INT NOT NULL PRIMARY KEY,
                Name NVARCHAR(50) NOT NULL,
                Tag NVARCHAR(10) NULL
            """);
        WriteFile("views/dbo.TaggedItems.sql",
            "CREATE VIEW dbo.TaggedItems WITH SCHEMABINDING AS SELECT i.Id, i.Tag FROM dbo.Items AS i;\nGO\n");
        await Create();
        _db.Execute("INSERT INTO dbo.Items (Id, Name, Tag) VALUES (1, N'alpha', N'one');");

        Write("Items", """
                Id INT NOT NULL PRIMARY KEY,
                Name NVARCHAR(50) NOT NULL
            """);
        File.Delete(Path.Combine(SchemaDir, "views", "dbo.TaggedItems.sql"));
        Write("Parts", PartsOfItems);

        // The view is gone from the desired state, so its drop runs - it does not depend on the column's.
        var plan = await DiffKeeping();
        Assert.Contains(plan.Actions, a => a.ObjectName == "dbo.TaggedItems");
        Assert.Contains(plan.Actions, a => a.ObjectName == "dbo.Parts");
        Assert.DoesNotContain(plan.Messages, m => m.Code is "SCHEMORPH015" or "SCHEMORPH017");
        await ApplyKeeping(plan);

        Assert.Equal(1, ColumnCount("Items", "Tag"));
        Assert.Equal(0, Count("SELECT COUNT(*) FROM sys.views WHERE name = 'TaggedItems'"));
    }

    [SkippableTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_alias_type_column_is_kept_and_so_is_a_type_the_desired_state_no_longer_declares(bool typeStaysDeclared)
    {
        const string type = "CREATE TYPE dbo.TagType FROM NVARCHAR(10) NOT NULL;\nGO\n";
        WriteFile("types/dbo.TagType.sql", type);
        Write("Items", """
                Id INT NOT NULL PRIMARY KEY,
                Name NVARCHAR(50) NOT NULL,
                Tag dbo.TagType
            """);
        await Create();
        _db.Execute("INSERT INTO dbo.Items (Id, Name, Tag) VALUES (1, N'alpha', N'one');");

        Write("Items", """
                Id INT NOT NULL PRIMARY KEY,
                Name NVARCHAR(50) NOT NULL
            """);
        if (!typeStaysDeclared) File.Delete(Path.Combine(SchemaDir, "types", "dbo.TagType.sql"));
        Write("Parts", PartsOfItems);

        // A type the column still uses cannot be dropped while the column stays, so the plan keeps it too and
        // reports its drop with the column's.
        var plan = await DiffKeeping();
        AssertKeptAndDependentRan(plan);
        var held = Assert.Single(plan.Excluded, e => e.ObjectName == "dbo.Items" && e.Statements is not null);
        Assert.Contains("DROP COLUMN [Tag]", held.Statements);
        Assert.Equal(!typeStaysDeclared, (held.Statements ?? string.Empty).Contains("DROP TYPE [dbo].[TagType]", StringComparison.Ordinal));
        await ApplyKeeping(plan);

        Assert.Equal("one", _db.Scalar<string>("SELECT CONVERT(NVARCHAR(10), Tag) FROM dbo.Items WHERE Id = 1"));
        Assert.Equal(1, Count("SELECT COUNT(*) FROM sys.types WHERE name = 'TagType'"));
    }

    [SkippableFact]
    public async Task A_sequence_the_withheld_columns_default_draws_from_is_kept_with_it()
    {
        WriteFile("sequences/dbo.TagSeq.sql", "CREATE SEQUENCE dbo.TagSeq AS INT START WITH 100;\nGO\n");
        Write("Items", """
                Id INT NOT NULL PRIMARY KEY,
                Name NVARCHAR(50) NOT NULL,
                Tag INT NOT NULL CONSTRAINT DF_Items_Tag DEFAULT (NEXT VALUE FOR dbo.TagSeq)
            """);
        await Create();
        _db.Execute("INSERT INTO dbo.Items (Id, Name) VALUES (1, N'alpha');");

        Write("Items", """
                Id INT NOT NULL PRIMARY KEY,
                Name NVARCHAR(50) NOT NULL
            """);
        File.Delete(Path.Combine(SchemaDir, "sequences", "dbo.TagSeq.sql"));
        Write("Parts", PartsOfItems);

        var plan = await DiffKeeping();
        AssertKeptAndDependentRan(plan);
        var held = Assert.Single(plan.Excluded, e => e.ObjectName == "dbo.Items" && e.Statements is not null);
        Assert.Contains("DROP SEQUENCE [dbo].[TagSeq]", held.Statements);
        await ApplyKeeping(plan);

        // The default still draws from the sequence, so inserts that do not name the column keep working.
        _db.Execute("INSERT INTO dbo.Items (Id, Name) VALUES (2, N'beta');");
        Assert.Equal(1, Count("SELECT COUNT(*) FROM sys.sequences WHERE name = 'TagSeq'"));
        Assert.Equal(2, Count("SELECT COUNT(*) FROM dbo.Items WHERE Tag >= 100"));
    }

    [SkippableFact]
    public async Task A_function_the_withheld_computed_column_calls_is_kept_with_it()
    {
        // Built outside the tool: a function a computed column calls cannot be redefined in place, which is
        // a separate matter from keeping the column.
        _db.Execute("CREATE FUNCTION dbo.Shout(@text NVARCHAR(50)) RETURNS NVARCHAR(50) AS BEGIN RETURN UPPER(@text); END");
        _db.Execute("CREATE TABLE dbo.Items (Id INT NOT NULL PRIMARY KEY, Name NVARCHAR(50) NOT NULL, Loud AS dbo.Shout(Name));");
        _db.Execute("INSERT INTO dbo.Items (Id, Name) VALUES (1, N'alpha');");

        Write("Items", """
                Id INT NOT NULL PRIMARY KEY,
                Name NVARCHAR(50) NOT NULL
            """);
        Write("Parts", PartsOfItems);

        var plan = await DiffKeeping();
        AssertKeptAndDependentRan(plan);
        var held = Assert.Single(plan.Excluded, e => e.ObjectName == "dbo.Items" && e.Statements is not null);
        Assert.Contains("DROP FUNCTION [dbo].[Shout]", held.Statements);
        await ApplyKeeping(plan);

        Assert.Equal("ALPHA", _db.Scalar<string>("SELECT Loud FROM dbo.Items WHERE Id = 1"));
        Assert.Equal(1, Count("SELECT COUNT(*) FROM sys.objects WHERE name = 'Shout'"));
    }

    [SkippableFact]
    public async Task A_column_of_a_system_versioned_table_is_not_kept_so_the_table_is_withheld_whole_and_the_plan_says_why()
    {
        WriteFile("tables/dbo.Items.sql", """
            CREATE TABLE dbo.Items (
                Id INT NOT NULL PRIMARY KEY CLUSTERED,
                Name NVARCHAR(50) NOT NULL,
                Tag NVARCHAR(10) NULL,
                ValidFrom DATETIME2 GENERATED ALWAYS AS ROW START HIDDEN NOT NULL,
                ValidTo DATETIME2 GENERATED ALWAYS AS ROW END HIDDEN NOT NULL,
                PERIOD FOR SYSTEM_TIME (ValidFrom, ValidTo)
            ) WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = dbo.ItemsHistory));
            GO

            """);
        await Create();
        _db.Execute("INSERT INTO dbo.Items (Id, Name, Tag) VALUES (1, N'alpha', N'one');");

        WriteFile("tables/dbo.Items.sql", """
            CREATE TABLE dbo.Items (
                Id INT NOT NULL PRIMARY KEY CLUSTERED,
                Name NVARCHAR(50) NOT NULL,
                ValidFrom DATETIME2 GENERATED ALWAYS AS ROW START HIDDEN NOT NULL,
                ValidTo DATETIME2 GENERATED ALWAYS AS ROW END HIDDEN NOT NULL,
                PERIOD FOR SYSTEM_TIME (ValidFrom, ValidTo)
            ) WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = dbo.ItemsHistory));
            GO

            """);
        Write("Parts", PartsOfItems);

        // The history table is not a declared table the comparison can add a column to, so the column is not
        // put back; the change to both tables is withheld whole, nothing runs, and the plan names the reason.
        var plan = await DiffKeeping();
        Assert.Empty(plan.Actions);
        Assert.Contains(plan.Messages, m => m.Code == "SCHEMORPH017");
        Assert.Contains(plan.Excluded, e => e.ObjectName == "dbo.Items");
        Assert.Contains(plan.Excluded, e => e.ObjectName == "dbo.Parts");
        await ApplyKeeping(plan);

        Assert.Equal(1, ColumnCount("Items", "Tag"));
        Assert.Equal(1, ColumnCount("ItemsHistory", "Tag"));
        Assert.Equal(0, Count("SELECT COUNT(*) FROM sys.tables WHERE name = 'Parts'"));
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }
}
