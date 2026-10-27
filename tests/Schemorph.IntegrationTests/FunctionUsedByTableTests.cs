using Schemorph.Core.Operations;
using Schemorph.Provider.SqlServer;

namespace Schemorph.IntegrationTests;

/// <summary>
/// A table that calls a function - in a computed column, a default or a check - needs the function to exist
/// when the table is created, and the engine will not alter a function something is built on. Applying such
/// a state to an empty database must still work, and settle: the function was created on the way to the
/// table, so the later re-definition pass has nothing left to do for it.
/// </summary>
public sealed class FunctionUsedByTableTests : IDisposable
{
    private readonly TestDatabase _db = new();
    private readonly string _dir = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), $"schemorph-fn-{Guid.NewGuid():N}")).FullName;
    private readonly SqlServerProvider _provider = new();
    private readonly SqlServerLedgerStore _ledger = new();

    private string SchemaDir => Path.Combine(_dir, "schema");

    private void WriteFile(string relative, string content)
    {
        var path = Path.Combine(SchemaDir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private const string Shout = """
        CREATE FUNCTION dbo.Shout(@text NVARCHAR(50)) RETURNS NVARCHAR(50)
        AS
        BEGIN
            RETURN UPPER(@text);
        END
        GO

        """;

    [SkippableTheory]
    [InlineData("Loud AS dbo.Shout(Name)")]
    [InlineData("Loud NVARCHAR(50) NOT NULL CONSTRAINT DF_Items_Loud DEFAULT (dbo.Shout(N'quiet'))")]
    [InlineData("Loud NVARCHAR(50) NULL CONSTRAINT CK_Items_Loud CHECK (Loud IS NULL OR Loud = dbo.Shout(Loud))")]
    public async Task A_function_a_table_calls_applies_to_an_empty_database_and_settles(string loudColumn)
    {
        WriteFile("functions/dbo.Shout.sql", Shout);
        WriteFile("tables/dbo.Items.sql", $"""
            CREATE TABLE dbo.[Items] (
                Id INT NOT NULL PRIMARY KEY,
                Name NVARCHAR(50) NOT NULL,
                {loudColumn}
            );
            GO

            """);

        var outcome = await ApplyOperation.RunAsync(_provider, _ledger, new ApplyOperation.Request(SchemaDir, _db.Url));
        Assert.True(outcome.Success, string.Join("; ", outcome.Errors.Select(e => e.Text)));
        Assert.Equal(1, _db.Scalar<int>("SELECT COUNT(*) FROM sys.objects WHERE name = 'Shout' AND type = 'FN'"));

        // A second pass has nothing to do: the ledger knows the function and the plan is empty.
        var again = await DiffOperation.RunAsync(_provider, _ledger, SchemaDir, _db.Url, allowDestructive: false);
        Assert.True(again.Success, string.Join("; ", again.Errors.Select(e => e.Text)));
        Assert.Empty(again.Plan!.Actions);
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }
}
