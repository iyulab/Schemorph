using Schemorph.Core.Operations;
using Schemorph.Core.Providers;

namespace Schemorph.Core.Tests;

/// <summary>
/// The lock-timeout option, judged without a database: how it is read from the command line or an
/// MCP argument, and that a provider which cannot honor it is refused before anything is touched.
/// The behavior on a real server (the setting reaching the transaction, a contended statement
/// failing and rolling back) is in the PostgreSQL provider's tests.
/// </summary>
public class ApplyLockTimeoutTests
{
    [Theory]
    [InlineData("500ms", 500)]
    [InlineData("30s", 30_000)]
    [InlineData("2m", 120_000)]
    [InlineData(" 7s ", 7_000)]
    [InlineData("2147483647ms", int.MaxValue)]
    public void A_whole_number_with_a_unit_is_a_lock_timeout(string text, long milliseconds)
    {
        Assert.True(ApplySessionOptions.TryParseLockTimeout(text, out var value, out var error), error);
        Assert.Equal(milliseconds, (long)value.TotalMilliseconds);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("30")]          // no unit: seconds to one reader, milliseconds to the server
    [InlineData("s")]
    [InlineData("ms")]
    [InlineData("1.5s")]
    [InlineData("-5s")]
    [InlineData("30 seconds")]
    [InlineData("1h")]
    [InlineData("0s")]          // 0 means "no timeout" on the server — say it by leaving the option out
    [InlineData("0ms")]
    [InlineData("2147483648ms")] // one more millisecond than the server can hold
    [InlineData("99999999999999999999s")]
    public void Anything_else_is_refused_with_a_message(string text)
    {
        Assert.False(ApplySessionOptions.TryParseLockTimeout(text, out _, out var error));
        Assert.NotEmpty(error);
    }

    [Fact]
    public async Task A_provider_whose_apply_is_not_one_transaction_refuses_a_lock_timeout_before_touching_anything()
    {
        var provider = new FakeProvider();   // Partial: opens no apply session
        var request = new ApplyOperation.Request("schema-dir-that-is-never-read", "unused", LockTimeout: TimeSpan.FromSeconds(5));

        var ex = await Assert.ThrowsAsync<UnsupportedByProviderException>(
            () => ApplyOperation.RunAsync(provider, new FakeLedger(), request));

        Assert.Equal("not_implemented", ex.ToError().Code);
        Assert.Contains("lock timeout", ex.Message);
        Assert.Empty(provider.SessionOptionsSeen);
        Assert.Empty(provider.ExecutedScripts);
    }
}
