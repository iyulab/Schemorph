using Schemorph.Provider.SqlServer;

namespace Schemorph.Provider.SqlServer.Tests;

/// <summary>
/// Programmable-object analysis is pure desired-state work (files → TSqlModel),
/// so it is unit-testable without a database.
/// </summary>
public sealed class AnalyzeProgrammablesTests : IDisposable
{
    private readonly string _dir = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), $"schemorph-analyze-{Guid.NewGuid():N}")).FullName;
    private readonly SqlServerProvider _provider = new();

    private void WriteFile(string relativePath, string content)
    {
        var path = Path.Combine(_dir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private void WriteCustomerSchema()
    {
        WriteFile(@"tables\dbo.Customers.sql", """
            CREATE TABLE dbo.Customers (Id INT NOT NULL PRIMARY KEY, Name NVARCHAR(100) NOT NULL);
            """);
        WriteFile(@"views\dbo.CustomerNames.sql", """
            CREATE VIEW dbo.CustomerNames AS SELECT Id, Name FROM dbo.Customers;
            """);
        WriteFile(@"procedures\dbo.GetCustomerNames.sql", """
            CREATE PROCEDURE dbo.GetCustomerNames AS SELECT Id, Name FROM dbo.CustomerNames;
            """);
    }

    [Fact]
    public async Task Reports_programmable_objects_with_their_defining_file()
    {
        WriteCustomerSchema();

        var analysis = await _provider.AnalyzeProgrammablesAsync(_dir);

        Assert.Empty(analysis.Messages);
        Assert.Equal(2, analysis.Objects.Count);   // the table is structural, not programmable

        var view = Assert.Single(analysis.Objects, o => o.ObjectName == "dbo.CustomerNames");
        Assert.Equal("View", view.ObjectType);
        Assert.EndsWith("dbo.CustomerNames.sql", view.FilePath);

        var proc = Assert.Single(analysis.Objects, o => o.ObjectName == "dbo.GetCustomerNames");
        Assert.Equal("Procedure", proc.ObjectType);
    }

    [Fact]
    public async Task Dependencies_are_restricted_to_the_programmable_set()
    {
        WriteCustomerSchema();

        var analysis = await _provider.AnalyzeProgrammablesAsync(_dir);

        var view = Assert.Single(analysis.Objects, o => o.ObjectName == "dbo.CustomerNames");
        Assert.Empty(view.DependsOn);   // depends only on the table — outside the set

        var proc = Assert.Single(analysis.Objects, o => o.ObjectName == "dbo.GetCustomerNames");
        Assert.Equal(new[] { "dbo.CustomerNames" }, proc.DependsOn);
    }

    [Fact]
    public async Task Apply_script_is_rewritten_to_create_or_alter()
    {
        WriteCustomerSchema();

        var analysis = await _provider.AnalyzeProgrammablesAsync(_dir);

        var view = Assert.Single(analysis.Objects, o => o.ObjectName == "dbo.CustomerNames");
        Assert.Contains("CREATE OR ALTER VIEW", view.ApplyScript);
        var proc = Assert.Single(analysis.Objects, o => o.ObjectName == "dbo.GetCustomerNames");
        Assert.Contains("CREATE OR ALTER PROCEDURE", proc.ApplyScript);
    }

    [Fact]
    public async Task Files_already_using_create_or_alter_are_left_intact()
    {
        WriteFile(@"views\dbo.V.sql", "CREATE OR ALTER VIEW dbo.V AS SELECT 1 AS One;");

        var analysis = await _provider.AnalyzeProgrammablesAsync(_dir);

        var view = Assert.Single(analysis.Objects);
        Assert.DoesNotContain("CREATE OR ALTER OR ALTER", view.ApplyScript);
        Assert.Contains("CREATE OR ALTER VIEW", view.ApplyScript);
    }

    [Fact]
    public async Task Triggers_are_part_of_the_programmable_set()
    {
        WriteFile(@"tables\dbo.Customers.sql", """
            CREATE TABLE dbo.Customers (Id INT NOT NULL PRIMARY KEY, Name NVARCHAR(100) NOT NULL);
            """);
        WriteFile(@"triggers\dbo.TrgCustomers.sql", """
            CREATE TRIGGER dbo.TrgCustomers ON dbo.Customers AFTER INSERT AS BEGIN SET NOCOUNT ON; END;
            """);

        var analysis = await _provider.AnalyzeProgrammablesAsync(_dir);

        var trigger = Assert.Single(analysis.Objects);
        Assert.Equal("DmlTrigger", trigger.ObjectType);
        Assert.Contains("CREATE OR ALTER TRIGGER", trigger.ApplyScript);
    }

    [Fact]
    public async Task A_file_holding_more_than_one_programmable_object_is_an_error()
    {
        // ADR-0002: one object per file; a misplaced file gets a clear error.
        WriteFile(@"views\two.sql", """
            CREATE VIEW dbo.A AS SELECT 1 AS One;
            GO
            CREATE VIEW dbo.B AS SELECT 2 AS Two;
            """);

        var analysis = await _provider.AnalyzeProgrammablesAsync(_dir);

        var message = Assert.Single(analysis.Messages, m => m.Severity == "Error");
        Assert.Contains("two.sql", message.Text);
        Assert.Contains("dbo.A", message.Text);
        Assert.Contains("dbo.B", message.Text);
    }

    [Fact]
    public async Task Invalid_desired_state_surfaces_model_errors()
    {
        WriteFile(@"views\dbo.Broken.sql", "CREATE VIEW dbo.Broken AS SELECT Id FROM dbo.DoesNotExist;");

        var analysis = await _provider.AnalyzeProgrammablesAsync(_dir);

        Assert.Empty(analysis.Objects);
        Assert.Contains(analysis.Messages, m => m.Severity == "Error");
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);
}
