using Schemorph.Core.Providers;
using Schemorph.Provider.SqlServer;

namespace Schemorph.Provider.SqlServer.Tests;

/// <summary>
/// A comparison that could not read the target in full silently omits changes to
/// whatever it missed. The plan must say so, or an incomplete result reads as an
/// in-sync database.
///
/// These pin the mapping. Which real connections actually produce which signal is
/// pinned by <c>RestrictedComparisonIntegrationTests</c> — the measurement that
/// decided this trigger.
/// </summary>
public class RestrictedComparisonTests
{
    private static RawMessage Warning(string code, string text) => new("Warning", code, text);
    private static RawMessage Error(string code, string text) => new("Error", code, text);

    [Fact]
    public void A_target_that_could_not_be_read_becomes_an_explicit_incompleteness_warning()
    {
        var messages = new[]
        {
            Error("SQL0", "The reverse engineering operation cannot continue because you do not have " +
                          "View Definition permission on the 'Sales' database."),
        };

        var surfaced = SqlServerProvider.RestrictedComparisonWarning(messages);

        Assert.NotNull(surfaced);
        Assert.Equal("SCHEMORPH008", surfaced.Code);
        Assert.Equal("Warning", surfaced.Severity);
        Assert.Contains("in sync", surfaced.Text);
        // The engine's own reason is echoed rather than a guessed one: "no
        // permission on the database" and "denied on one object" need different fixes.
        Assert.Contains("View Definition permission on the 'Sales' database", surfaced.Text);
    }

    [Fact]
    public void Every_reported_error_is_echoed()
    {
        var messages = new[]
        {
            Error("SQL0", "first reason"),
            Error("SQL0", "second reason"),
        };

        var surfaced = SqlServerProvider.RestrictedComparisonWarning(messages);

        Assert.NotNull(surfaced);
        Assert.Contains("first reason", surfaced.Text);
        Assert.Contains("second reason", surfaced.Text);
    }

    /// <summary>
    /// The regression this trigger exists for. A least-privilege login without the
    /// server-scoped grant draws this warning from DacFx and still reads every
    /// database object — measured, cycle 66. Firing on it warned every consumer
    /// following this tool's own least-privilege guidance that their complete plan
    /// might be incomplete.
    /// </summary>
    [Fact]
    public void The_server_scope_permission_warning_alone_is_not_incompleteness()
    {
        var messages = new[]
        {
            Warning("SQL0", "The login for the target does not have the VIEW ANY DEFINITION permission. " +
                            "The comparison will be restricted to database scoped elements if the source is a database."),
        };

        Assert.Null(SqlServerProvider.RestrictedComparisonWarning(messages));
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
