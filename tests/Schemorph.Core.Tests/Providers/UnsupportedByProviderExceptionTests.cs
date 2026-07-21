using Schemorph.Core.Providers;

namespace Schemorph.Core.Tests.Providers;

public class UnsupportedByProviderExceptionTests
{
    [Fact]
    public void Refusal_carries_the_unsupported_machine_contract()
    {
        var error = new UnsupportedByProviderException("postgres", "compare", supported: "inspect").ToError();

        Assert.Equal("not_implemented", error.Code);
        Assert.Equal("unsupported", error.Kind);
        Assert.Contains("postgres", error.Message);
        Assert.Contains("compare", error.Message);
    }

    [Fact]
    public void The_hint_names_what_the_provider_does_declare()
    {
        var error = new UnsupportedByProviderException("postgres", "apply", supported: "inspect").ToError();

        Assert.NotNull(error.Hint);
        Assert.Contains("inspect", error.Hint);
    }

    [Fact]
    public void A_provider_that_declares_nothing_offers_no_hint()
    {
        // A confident wrong direction is worse than silence — the same rule the
        // inspect hint was removed under (cycle 70).
        var error = new UnsupportedByProviderException("postgres", "apply").ToError();

        Assert.Null(error.Hint);
    }
}
