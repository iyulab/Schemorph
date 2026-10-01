using Npgsql;

namespace Schemorph.Provider.Postgres.Tests;

using ProgrammableFile = PgDesiredState.ProgrammableFile;

/// <summary>
/// A view that exists live is rebuilt from its file in a scratch schema to compare columns. When
/// the file cannot be built there — a typo, a column the desired state does not declare — that is
/// a fault in the desired state, and the user needs to know which file. The engine's own error
/// says what is missing, never where; it used to escape as the whole diff's failure with no file
/// named.
/// </summary>
public class ViewProbeFailureTests
{
    [SkippableFact]
    public async Task A_view_file_that_cannot_be_built_is_reported_by_name_and_file()
    {
        await using var live = await PgTestSchema.CreateAsync("""
            CREATE TABLE t (a int, b int);
            CREATE VIEW v1 AS SELECT a FROM t;
            CREATE VIEW v2 AS SELECT b FROM t;
            """);
        var url = new NpgsqlConnectionStringBuilder(PgTestSchema.ServerUrl!) { SearchPath = live.Name }
            .ConnectionString;

        var analysis = PgProgrammables.Analyze(new[]
        {
            new ProgrammableFile("views/v1.sql", "CREATE VIEW v1 AS SELECT a, 1 AS c FROM t;"),
            new ProgrammableFile("views/v2.sql", "CREATE VIEW v2 AS SELECT b, bb FROM t;"),
        });

        var refined = await ViewRedefinePlanner.RefineAsync(
            analysis, new[] { "CREATE TABLE t (a int, b int);" }, url, live.Name, CancellationToken.None);

        var error = Assert.Single(refined.Messages);
        Assert.Equal("Error", error.Severity);
        Assert.Equal("SCHEMORPH013", error.Code);
        Assert.StartsWith("v2 (views/v2.sql):", error.Text);
        Assert.Contains("42703: column \"bb\" does not exist", error.Text);

        // The other view's probe is unaffected — one bad file does not hide what the rest would do.
        var v1 = refined.Objects.Single(o => o.ObjectName == "v1");
        Assert.Null(v1.RiskOverride);
    }
}
