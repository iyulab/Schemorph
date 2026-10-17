using Schemorph.Core.Operations;
using Schemorph.Provider.SqlServer;

namespace Schemorph.IntegrationTests;

/// <summary>
/// A view definition can compile and still fail on the rows already there — a cast the values overflow, a division by
/// zero. Nothing about the definition says so; only reading it does, and without a read the first to find out is every
/// query that touches the view after the apply reported success. The re-definition reads the view once before it commits
/// (<c>SqlServerProvider.RedefineAsync</c>), so the apply fails at the view and the previous definition stays.
/// </summary>
public sealed class ViewReadTests : IDisposable
{
    private readonly TestDatabase _db = new();
    private readonly string _dir = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), $"schemorph-viewread-{Guid.NewGuid():N}")).FullName;
    private readonly SqlServerProvider _provider = new();
    private readonly SqlServerLedgerStore _ledger = new();

    private string SchemaDir => Path.Combine(_dir, "schema");

    private void Write(string relativePath, string content)
    {
        var path = Path.Combine(SchemaDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private void WriteTable() => Write("tables/dbo.Lines.sql", """
        CREATE TABLE dbo.Lines (
            Id INT NOT NULL PRIMARY KEY,
            Price DECIMAL(18, 2) NOT NULL,
            ListPrice DECIMAL(18, 2) NOT NULL
        );
        GO

        """);

    private void WriteView(string ratio) => Write("views/dbo.LineRatios.sql", $"""
        CREATE VIEW dbo.LineRatios AS
        SELECT Id, {ratio} AS Ratio FROM dbo.Lines;
        GO

        """);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private async Task<ApplyOperation.Outcome> ApplyAsync() =>
        await ApplyOperation.RunAsync(_provider, _ledger, new ApplyOperation.Request(SchemaDir, _db.Url));

    [SkippableFact]
    public async Task A_new_view_definition_the_rows_overflow_fails_its_apply_and_keeps_the_previous_one()
    {
        WriteTable();
        WriteView("CAST(Price * 100 / ListPrice AS DECIMAL(12, 1))");
        var created = await ApplyAsync();
        Assert.True(created.Success, string.Join("; ", created.Errors.Select(e => e.Text)));
        _db.Execute("INSERT INTO dbo.Lines (Id, Price, ListPrice) VALUES (1, 12000, 1), (2, 50, 100);");

        // Narrower than the first row's 1,200,000.0 — the definition itself is valid.
        WriteView("CAST(Price * 100 / ListPrice AS DECIMAL(5, 1))");
        var outcome = await ApplyAsync();

        Assert.False(outcome.Success);
        Assert.Equal(ApplyOperation.FailureStage.Redefine, outcome.Stage);
        var error = string.Join("; ", outcome.Errors.Select(e => e.Text));
        Assert.Contains("LineRatios", error);
        Assert.Contains("8115", error);

        // The previous definition is in place and still reads.
        Assert.Contains("DECIMAL(12, 1)", _db.Scalar<string>(
            "SELECT definition FROM sys.sql_modules WHERE object_id = OBJECT_ID(N'dbo.LineRatios')"));
        Assert.Equal(2, _db.Scalar<int>("SELECT COUNT(*) FROM dbo.LineRatios"));
    }

    [SkippableFact]
    public async Task A_new_view_definition_that_divides_a_row_by_zero_fails_its_apply()
    {
        WriteTable();
        WriteView("Price");
        Assert.True((await ApplyAsync()).Success);
        _db.Execute("INSERT INTO dbo.Lines (Id, Price, ListPrice) VALUES (1, 10, 0);");

        WriteView("Price / ListPrice");
        var outcome = await ApplyAsync();

        Assert.False(outcome.Success);
        Assert.Contains("8134", string.Join("; ", outcome.Errors.Select(e => e.Text)));
    }

    [SkippableFact]
    public async Task A_narrower_definition_the_rows_fit_is_applied()
    {
        WriteTable();
        WriteView("CAST(Price * 100 / ListPrice AS DECIMAL(12, 1))");
        Assert.True((await ApplyAsync()).Success);
        _db.Execute("INSERT INTO dbo.Lines (Id, Price, ListPrice) VALUES (1, 50, 100);");

        WriteView("CAST(Price * 100 / ListPrice AS DECIMAL(5, 1))");
        var outcome = await ApplyAsync();

        Assert.True(outcome.Success, string.Join("; ", outcome.Errors.Select(e => e.Text)));
        Assert.Contains("DECIMAL(5, 1)", _db.Scalar<string>(
            "SELECT definition FROM sys.sql_modules WHERE object_id = OBJECT_ID(N'dbo.LineRatios')"));
    }
}
