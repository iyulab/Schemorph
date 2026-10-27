using Schemorph.Core.Operations;
using Schemorph.Provider.SqlServer;

namespace Schemorph.IntegrationTests;

/// <summary>
/// Changing the body of a function that a computed column, a default or a check calls. The engine refuses to
/// alter such a function, so the apply stops - cleanly, rolled back, naming the dependent - and the way
/// through is to take the dependents out of the desired state, change the function, and declare them again.
/// Both halves are pinned: the refusal, and that the way through works and loses no data.
/// </summary>
public sealed class FunctionBodyChangeTests : IDisposable
{
    private readonly TestDatabase _db = new();
    private readonly string _dir = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), $"schemorph-fnbody-{Guid.NewGuid():N}")).FullName;
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

    private static string Trimmed => Shout.Replace("UPPER(@text)", "UPPER(LTRIM(RTRIM(@text)))", StringComparison.Ordinal);

    private void WriteItems(string loudColumn) => WriteFile("tables/dbo.Items.sql", $"""
        CREATE TABLE dbo.[Items] (
            Id INT NOT NULL PRIMARY KEY,
            Name NVARCHAR(50) NOT NULL{(loudColumn.Length == 0 ? "" : ",\n    " + loudColumn)}
        );
        GO

        """);

    private async Task<ApplyOperation.Outcome> Apply(bool allowDestructive = false) =>
        await ApplyOperation.RunAsync(_provider, _ledger,
            new ApplyOperation.Request(SchemaDir, _db.Url, AllowDestructive: allowDestructive));

    private bool BodyIsTrimmed() =>
        _db.Scalar<int>("SELECT CASE WHEN OBJECT_DEFINITION(OBJECT_ID('dbo.Shout')) LIKE '%LTRIM%' THEN 1 ELSE 0 END") == 1;

    // The column as the desired state declares it with its dependent, and as it is declared without.
    public static TheoryData<string, string, string> Dependents => new()
    {
        { "computed column", "Loud AS dbo.Shout(Name)", "" },
        { "default", "Loud NVARCHAR(50) NOT NULL CONSTRAINT DF_Items_Loud DEFAULT (dbo.Shout(N'quiet'))", "Loud NVARCHAR(50) NOT NULL" },
        { "check", "Loud NVARCHAR(50) NULL CONSTRAINT CK_Items_Loud CHECK (Loud IS NULL OR Loud = dbo.Shout(Loud))", "Loud NVARCHAR(50) NULL" },
    };

    [SkippableTheory]
    [MemberData(nameof(Dependents))]
    public async Task Changing_the_body_stops_the_apply_cleanly_and_names_the_dependent(string kind, string withDependent, string _)
    {
        WriteFile("functions/dbo.Shout.sql", Shout);
        WriteItems(withDependent);
        var created = await Apply();
        Assert.True(created.Success, string.Join("; ", created.Errors.Select(e => e.Text)));
        _db.Execute("INSERT INTO dbo.Items (Id, Name) VALUES (1, N'alpha');");

        WriteFile("functions/dbo.Shout.sql", Trimmed);

        // The plan shows the re-definition like any other; it is the engine that refuses.
        var diff = await DiffOperation.RunAsync(_provider, _ledger, SchemaDir, _db.Url, allowDestructive: false);
        Assert.True(diff.Success);
        Assert.Contains(diff.Plan!.Actions, a => a.ObjectName == "dbo.Shout");

        var outcome = await Apply();
        Assert.False(outcome.Success, kind);
        Assert.Equal(ApplyOperation.FailureStage.Redefine, outcome.Stage);
        var error = Assert.Single(outcome.Errors);
        Assert.Equal("redefine_execution_failed", error.Code);
        Assert.Contains("3729", error.Text);
        Assert.Contains("Cannot ALTER 'dbo.Shout'", error.Text);

        // Rolled back: the function is as it was and the row is intact.
        Assert.False(BodyIsTrimmed());
        Assert.Equal(1, _db.Scalar<int>("SELECT COUNT(*) FROM dbo.Items WHERE Id = 1"));
    }

    [SkippableTheory]
    [MemberData(nameof(Dependents))]
    public async Task Taking_the_dependent_out_changing_the_function_and_declaring_it_again_works_without_losing_data(
        string kind, string withDependent, string withoutDependent)
    {
        WriteFile("functions/dbo.Shout.sql", Shout);
        WriteItems(withDependent);
        var created = await Apply();
        Assert.True(created.Success, string.Join("; ", created.Errors.Select(e => e.Text)));
        _db.Execute(kind == "computed column"
            ? "INSERT INTO dbo.Items (Id, Name) VALUES (1, N'alpha');"
            : "INSERT INTO dbo.Items (Id, Name, Loud) VALUES (1, N'alpha', N'KEPT');");

        // 1. The dependent comes out of the desired state (a computed column's drop needs the destructive switch).
        WriteItems(withoutDependent);
        var out1 = await Apply(allowDestructive: true);
        Assert.True(out1.Success, string.Join("; ", out1.Errors.Select(e => e.Text)));

        // 2. The function changes.
        WriteFile("functions/dbo.Shout.sql", Trimmed);
        var out2 = await Apply();
        Assert.True(out2.Success, string.Join("; ", out2.Errors.Select(e => e.Text)));
        Assert.True(BodyIsTrimmed());

        // 3. The dependent is declared again.
        WriteItems(withDependent);
        var out3 = await Apply();
        Assert.True(out3.Success, string.Join("; ", out3.Errors.Select(e => e.Text)));

        Assert.Equal(1, _db.Scalar<int>("SELECT COUNT(*) FROM dbo.Items WHERE Id = 1"));
        if (kind != "computed column")
        {
            Assert.Equal("KEPT", _db.Scalar<string>("SELECT Loud FROM dbo.Items WHERE Id = 1"));
        }
        else
        {
            Assert.Equal("ALPHA", _db.Scalar<string>("SELECT Loud FROM dbo.Items WHERE Id = 1"));
        }
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }
}
