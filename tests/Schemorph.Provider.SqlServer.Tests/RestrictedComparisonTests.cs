using Schemorph.Core.Providers;
using Schemorph.Provider.SqlServer;

namespace Schemorph.Provider.SqlServer.Tests;

/// <summary>
/// A restricted comparison (the login cannot read object definitions) silently
/// omits changes to whatever DacFx could not see. The plan must say so, or an
/// incomplete result reads as an in-sync database.
/// </summary>
public class RestrictedComparisonTests
{
    private static RawMessage Warning(string code, string text) => new("Warning", code, text);

    [Fact]
    public void A_view_any_definition_restriction_becomes_an_explicit_incompleteness_warning()
    {
        var messages = new[]
        {
            Warning("SQL0", "The login for the target does not have the VIEW ANY DEFINITION permission. " +
                            "The comparison will be restricted to database scoped elements if the source is a database."),
        };

        var surfaced = SqlServerProvider.RestrictedComparisonWarning(messages);

        Assert.NotNull(surfaced);
        Assert.Equal("SCHEMORPH008", surfaced.Code);
        Assert.Equal("Warning", surfaced.Severity);
        Assert.Contains("VIEW ANY DEFINITION", surfaced.Text);
        Assert.Contains("in sync", surfaced.Text);
    }

    [Fact]
    public void An_unrestricted_comparison_adds_no_warning()
    {
        var messages = new[]
        {
            Warning("SCHEMORPH102", "dbo.T: this change rebuilds the table"),
        };

        Assert.Null(SqlServerProvider.RestrictedComparisonWarning(messages));
    }

    [Fact]
    public void No_messages_means_no_warning()
    {
        Assert.Null(SqlServerProvider.RestrictedComparisonWarning(Array.Empty<RawMessage>()));
    }
}
