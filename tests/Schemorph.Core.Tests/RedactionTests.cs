using Schemorph.Core;
using Schemorph.Core.Ledger;

namespace Schemorph.Core.Tests;

public sealed class RedactionTests
{
    [Theory]
    [InlineData("Server=x;Password=hunter2;Encrypt=False", "Server=x;Password=***;Encrypt=False")]
    [InlineData("Server=x;PWD=hunter2", "Server=x;PWD=***")]
    [InlineData("Login failed. password = s3cr3t!", "Login failed. password=***")]
    [InlineData("Two: Password=a;...Pwd=b;", "Two: Password=***;...Pwd=***;")]
    public void Password_material_is_masked(string input, string expected)
    {
        Assert.Equal(expected, Redaction.Redact(input));
    }

    [Fact]
    public void Text_without_secrets_passes_through_unchanged()
    {
        const string text = "Cannot insert the value NULL into column 'Id'.";
        Assert.Equal(text, Redaction.Redact(text));
    }

    [Fact]
    public async Task Failure_rows_are_redacted_before_they_persist()
    {
        // The ledger is a persisted output channel: error text that embeds a
        // connection string must never store the password.
        var ledger = new FakeLedger();

        await ledger.AppendFailureBestEffortAsync("conn", new LedgerEntry(
            "migration", "V1__x.sql", "Run", null,
            Succeeded: false, Detail: "Cannot open 'Server=x;Password=hunter2'."));

        var entry = Assert.Single(ledger.Entries);
        Assert.DoesNotContain("hunter2", entry.Detail);
        Assert.Contains("Password=***", entry.Detail);
    }
}
