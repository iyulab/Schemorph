using Schemorph.Core.Providers;

namespace Schemorph.Core.Tests.Providers;

public class DesiredStateFileTests
{
    [Theory]
    [InlineData("dbo.Orders")]
    [InlineData("vibebase_control.Workspaces")]
    [InlineData("public.über_tabelle")]
    public void Clean_names_pass_through_verbatim(string name)
    {
        Assert.Equal(name, DesiredStateFile.SafeSegment(name));
    }

    [Fact]
    public void Offending_characters_are_replaced_and_the_name_is_disambiguated()
    {
        var segment = DesiredStateFile.SafeSegment("public.we\"ird");

        Assert.DoesNotContain("\"", segment);
        Assert.StartsWith("public.we_ird-", segment);
    }

    [Fact]
    public void Two_different_identifiers_never_collapse_into_one_file_name()
    {
        // Both sanitize to the same characters; the hash suffix keeps them apart.
        Assert.NotEqual(
            DesiredStateFile.SafeSegment("a/b"),
            DesiredStateFile.SafeSegment("a\\b"));
    }

    [Fact]
    public void Path_separators_cannot_escape_the_kind_directory()
    {
        Assert.DoesNotContain("/", DesiredStateFile.SafeSegment("a/../../b"));
        Assert.DoesNotContain("\\", DesiredStateFile.SafeSegment("a\\..\\b"));
    }

    [Fact]
    public void Trailing_dots_and_spaces_are_not_left_behind()
    {
        // "name." and "name " are invalid or self-renaming on Windows.
        Assert.StartsWith("odd-", DesiredStateFile.SafeSegment("odd."));
        Assert.StartsWith("odd-", DesiredStateFile.SafeSegment("odd "));
    }
}
