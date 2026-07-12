namespace Schemorph.Provider.SqlServer.Tests;

/// <summary>
/// The loader is the desired-state boundary: deploy scripts and seed DML are
/// skipped loudly, while anything declarative loads. The asymmetry under test:
/// a false skip would surface as a phantom DROP, so skipping requires positive
/// evidence — a merely-broken DDL file must be an attributed error, never a skip.
/// </summary>
public sealed class DesiredStateLoaderTests : IDisposable
{
    private readonly string _dir = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), $"schemorph-loader-{Guid.NewGuid():N}")).FullName;

    private string WriteFile(string relativePath, string content)
    {
        var path = Path.Combine(_dir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void Declarative_ddl_loads_as_model_files()
    {
        WriteFile(@"tables\dbo.Customers.sql", """
            CREATE TABLE dbo.Customers (Id INT NOT NULL PRIMARY KEY, Name NVARCHAR(100) NOT NULL);
            GO
            CREATE INDEX IX_Customers_Name ON dbo.Customers (Name);
            """);

        var result = DesiredStateLoader.Load(_dir);

        Assert.Single(result.ModelFiles);
        Assert.Empty(result.Warnings);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Ssms_scripted_files_with_set_options_are_not_skipped()
    {
        // SSMS-generated scripts wrap DDL in SET ANSI_NULLS / QUOTED_IDENTIFIER —
        // a false skip here would drop a real object from the model.
        WriteFile(@"tables\dbo.T.sql", """
            SET ANSI_NULLS ON
            GO
            SET QUOTED_IDENTIFIER ON
            GO
            CREATE TABLE dbo.T (Id INT NOT NULL PRIMARY KEY);
            """);

        var result = DesiredStateLoader.Load(_dir);

        Assert.Single(result.ModelFiles);
        Assert.Empty(result.Warnings);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Sqlcmd_include_directive_is_skipped_with_a_warning()
    {
        // The SSDT PostDeployment case that broke real-world adoption (cycle-21).
        var path = WriteFile(@"Scripts\Script.PostDeployment.sql",
            ":r ..\\Scripts_gen\\Script.PostDeployment.RefreshViews.sql");

        var result = DesiredStateLoader.Load(_dir);

        Assert.Empty(result.ModelFiles);
        Assert.Empty(result.Errors);
        var warning = Assert.Single(result.Warnings);
        Assert.Equal("SCHEMORPH005", warning.Code);
        Assert.Contains(path, warning.Text);
    }

    [Fact]
    public void Sqlcmd_examples_inside_comments_do_not_skip_a_model_file()
    {
        // The false-skip guard: SSDT's Pre/Post-Deployment *templates* carry SQLCMD
        // examples in comments; a model file with such comments must still load.
        WriteFile(@"tables\dbo.Documented.sql", """
            /*
             Use SQLCMD syntax to include a file:
             Example:      :r .\myfile.sql
             Use SQLCMD syntax to reference a variable:
             Example:      :setvar TableName MyTable
                           SELECT * FROM [$(TableName)]
            */
            CREATE TABLE dbo.Documented (Id INT NOT NULL PRIMARY KEY);
            """);

        var result = DesiredStateLoader.Load(_dir);

        Assert.Single(result.ModelFiles);
        Assert.Empty(result.Warnings);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Comment_only_files_load_as_harmless_noops()
    {
        // SSDT Pre-Deployment templates are often all-comment; they add nothing
        // to the model and must neither warn nor error.
        WriteFile(@"Scripts\Script.PreDeployment.sql", """
            /*
             Pre-Deployment Script Template
             Example:      :setvar TableName MyTable
            */
            """);

        var result = DesiredStateLoader.Load(_dir);

        Assert.Single(result.ModelFiles);
        Assert.Empty(result.Warnings);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Sqlcmd_variable_usage_is_skipped_with_a_warning()
    {
        WriteFile(@"scripts\env.sql", "ALTER DATABASE [$(DatabaseName)] SET RECOVERY SIMPLE;");

        var result = DesiredStateLoader.Load(_dir);

        Assert.Empty(result.ModelFiles);
        var warning = Assert.Single(result.Warnings);
        Assert.Equal("SCHEMORPH005", warning.Code);
    }

    [Fact]
    public void Exec_only_scripts_are_skipped_with_a_warning()
    {
        // The SSDT RefreshViews case: valid T-SQL, but imperative — not desired state.
        var path = WriteFile(@"Scripts_gen\RefreshViews.sql", """
            EXEC sp_refreshview N'[dbo].[CustomerNames]';
            EXEC sp_refreshview N'[dbo].[OrderTotals]';
            """);

        var result = DesiredStateLoader.Load(_dir);

        Assert.Empty(result.ModelFiles);
        Assert.Empty(result.Errors);
        var warning = Assert.Single(result.Warnings);
        Assert.Equal("SCHEMORPH006", warning.Code);
        Assert.Contains(path, warning.Text);
        Assert.Contains("EXECUTE", warning.Text);
    }

    [Fact]
    public void Seed_dml_is_skipped_with_a_warning()
    {
        WriteFile(@"seed\categories.sql", """
            INSERT INTO dbo.Category (Code, Name) VALUES ('A', 'Alpha');
            """);

        var result = DesiredStateLoader.Load(_dir);

        Assert.Empty(result.ModelFiles);
        var warning = Assert.Single(result.Warnings);
        Assert.Equal("SCHEMORPH006", warning.Code);
    }

    [Fact]
    public void Mixed_ddl_and_dml_skips_the_whole_file_loudly()
    {
        // One concern per file: a half-honored file would hide the problem.
        WriteFile(@"tables\dbo.Lookup.sql", """
            CREATE TABLE dbo.Lookup (Id INT NOT NULL PRIMARY KEY, Name NVARCHAR(50) NOT NULL);
            GO
            INSERT INTO dbo.Lookup (Id, Name) VALUES (1, 'Default');
            """);

        var result = DesiredStateLoader.Load(_dir);

        Assert.Empty(result.ModelFiles);
        var warning = Assert.Single(result.Warnings);
        Assert.Equal("SCHEMORPH006", warning.Code);
    }

    [Fact]
    public void Broken_ddl_without_sqlcmd_markers_is_an_attributed_error_not_a_skip()
    {
        // The asymmetry guard: a typo'd model file must fail the compare with the
        // file named — silently skipping it would plan a DROP of the real object.
        var path = WriteFile(@"tables\dbo.Broken.sql",
            "CREATE TABEL dbo.Broken (Id INT);");   // deliberate typo

        var result = DesiredStateLoader.Load(_dir);

        Assert.Empty(result.ModelFiles);
        Assert.Empty(result.Warnings);
        var error = Assert.Single(result.Errors);
        Assert.Equal("SCHEMORPH007", error.Code);
        Assert.Contains(path, error.Text);
    }

    [Fact]
    public void Imperative_bodies_inside_programmable_objects_do_not_mark_the_file_imperative()
    {
        // Procedures contain IF/DECLARE/SELECT in their body — only *top-level*
        // imperative statements make a file a script.
        WriteFile(@"procedures\dbo.GetThing.sql", """
            CREATE PROCEDURE dbo.GetThing @Id INT AS
            BEGIN
                SET NOCOUNT ON;
                DECLARE @Name NVARCHAR(100);
                IF @Id > 0 SELECT @Name = 'x';
                SELECT @Name AS Name;
            END;
            """);

        var result = DesiredStateLoader.Load(_dir);

        Assert.Single(result.ModelFiles);
        Assert.Empty(result.Warnings);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Create_or_alter_files_load_as_model_files()
    {
        WriteFile(@"views\dbo.V.sql", "CREATE OR ALTER VIEW dbo.V AS SELECT 1 AS One;");

        var result = DesiredStateLoader.Load(_dir);

        Assert.Single(result.ModelFiles);
        Assert.Empty(result.Warnings);
        Assert.Empty(result.Errors);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);
}
