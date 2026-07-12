using Schemorph.Provider.SqlServer;

namespace Schemorph.Provider.SqlServer.Tests;

/// <summary>
/// The parser-backed batch splitter: only real GO separators split, original
/// text (comments included) stays with its batch, unreadable scripts fall
/// back to line-based splitting.
/// </summary>
public sealed class SqlBatchSplitterTests
{
    [Fact]
    public void Splits_on_go_separators_case_insensitively()
    {
        var batches = SqlBatchSplitter.Split("SELECT 1;\ngo\nSELECT 2;\nGO\nSELECT 3;").ToList();

        Assert.Equal(new[] { "SELECT 1;", "SELECT 2;", "SELECT 3;" }, batches);
    }

    [Fact]
    public void Go_inside_a_string_literal_does_not_split()
    {
        var batches = SqlBatchSplitter.Split("INSERT INTO dbo.T (Word) VALUES ('\nGO\n');").ToList();

        var batch = Assert.Single(batches);
        Assert.Contains("GO", batch);
    }

    [Fact]
    public void Go_inside_comments_does_not_split()
    {
        var script = """
            /* the old tool wrote
            GO
            here */
            SELECT 1; -- GO
            """;

        Assert.Single(SqlBatchSplitter.Split(script));
    }

    [Fact]
    public void Comments_stay_with_their_batch_verbatim()
    {
        // LiveDefinitionMatcher compares batch text against sys.sql_modules,
        // which stores the deployed batch verbatim — a leading comment is part
        // of the definition and must not be detached from it.
        var script = "-- audit view\nCREATE VIEW dbo.V AS SELECT 1 AS One\nGO\nCREATE VIEW dbo.W AS SELECT 2 AS Two";

        var batches = SqlBatchSplitter.Split(script).ToList();

        Assert.Equal(2, batches.Count);
        Assert.StartsWith("-- audit view", batches[0]);
        Assert.Contains("dbo.V", batches[0]);
    }

    [Fact]
    public void Unparseable_scripts_fall_back_to_line_based_splitting()
    {
        // A SQLCMD-mode deployment script does not parse as T-SQL; the
        // pre-parser behavior (a line that is exactly GO separates) holds.
        var script = ":setvar DatabaseName \"Db\"\nGO\nPRINT N'x';\nGO\nDROP TABLE [dbo].[Gone];";

        var batches = SqlBatchSplitter.Split(script).ToList();

        Assert.Equal(3, batches.Count);
        Assert.Contains("DROP TABLE", batches[2]);
    }
}
