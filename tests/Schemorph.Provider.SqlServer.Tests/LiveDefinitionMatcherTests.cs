namespace Schemorph.Provider.SqlServer.Tests;

/// <summary>
/// Reconciliation matching is deliberately strict: only the equivalences
/// Schemorph itself introduces (line endings, outer whitespace, CREATE OR ALTER
/// vs CREATE, the file's GO batching). A false match silently adopts a
/// differing object; a false mismatch merely costs one idempotent redefinition.
/// </summary>
public sealed class LiveDefinitionMatcherTests
{
    private const string Live = "CREATE VIEW dbo.V\nAS\nSELECT 1 AS One";

    [Fact]
    public void Identical_text_matches()
        => Assert.True(LiveDefinitionMatcher.Matches(Live, Live));

    [Fact]
    public void Crlf_and_trailing_newline_differences_match()
        => Assert.True(LiveDefinitionMatcher.Matches(
            "CREATE VIEW dbo.V\r\nAS\r\nSELECT 1 AS One\r\n", Live));

    [Fact]
    public void File_trailing_go_matches()
        => Assert.True(LiveDefinitionMatcher.Matches(
            "CREATE VIEW dbo.V\nAS\nSELECT 1 AS One\nGO\n", Live));

    [Fact]
    public void Create_or_alter_in_either_side_matches_plain_create()
    {
        Assert.True(LiveDefinitionMatcher.Matches(
            "CREATE OR ALTER VIEW dbo.V\nAS\nSELECT 1 AS One", Live));
        Assert.True(LiveDefinitionMatcher.Matches(
            Live, "CREATE OR ALTER VIEW dbo.V\nAS\nSELECT 1 AS One"));
    }

    [Fact]
    public void Multi_batch_file_matches_when_one_batch_is_the_definition()
        => Assert.True(LiveDefinitionMatcher.Matches(
            "SET ANSI_NULLS ON\nGO\n" + Live + "\nGO\n", Live));

    [Fact]
    public void Trailing_semicolon_difference_matches()
        => Assert.True(LiveDefinitionMatcher.Matches(
            "CREATE VIEW dbo.V\nAS\nSELECT 1 AS One;\nGO\n", Live));

    [Fact]
    public void Different_body_does_not_match()
        => Assert.False(LiveDefinitionMatcher.Matches(
            "CREATE VIEW dbo.V\nAS\nSELECT 2 AS Two", Live));

    [Fact]
    public void Comment_differences_do_not_match()
    {
        // Comments live inside sys.sql_modules definitions; a differing comment
        // is a differing definition. Strictness is the safety property here.
        Assert.False(LiveDefinitionMatcher.Matches(
            "-- header\n" + Live, Live));
    }

    [Fact]
    public void Internal_whitespace_differences_do_not_match()
        => Assert.False(LiveDefinitionMatcher.Matches(
            "CREATE VIEW  dbo.V\nAS\nSELECT 1 AS One", Live));
}
