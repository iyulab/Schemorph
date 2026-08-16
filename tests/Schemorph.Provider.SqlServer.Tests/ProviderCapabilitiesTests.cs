using Schemorph.Core.Providers;
using Schemorph.Provider.SqlServer;

namespace Schemorph.Provider.SqlServer.Tests;

public class ProviderCapabilitiesTests
{
    // The SQL Server declaration is the parity yardstick (dev plan §1): a
    // second provider's capability list grows one line per slice toward THIS
    // list, and parity is reached when the two match. Changing it is changing
    // what "SQL Server level" means — deliberate, never incidental.
    [Fact]
    public void Declares_the_full_capability_surface()
    {
        var declared = new SqlServerProvider().Capabilities.Declared;

        Assert.Equal(
            new[]
            {
                "inspect", "tables", "columns", "constraints", "schemas", "indexes",
                "views", "functions", "triggers", "procedures", "migrations",
            },
            declared);
    }

    /// <summary>
    /// D2: a consumer reading the CLI manifest's <c>provider.vocabulary</c> has
    /// to trust that it really is the full line set, not a second copy that can
    /// drift from what "the full surface" above actually asserts. This provider
    /// IS the vocabulary — pinning identity, not just equal content.
    /// </summary>
    [Fact]
    public void Its_declared_surface_is_the_capability_vocabulary_itself()
    {
        Assert.Same(CapabilityVocabulary.All, new SqlServerProvider().Capabilities.Declared);
    }

    [Fact]
    public void Declares_partial_atomicity()
    {
        // ADR-0004 addendum: DacFx owns the publish connection, so stages
        // commit independently — the declaration must not claim more than the
        // tool controls.
        Assert.Equal(ApplyAtomicity.Partial, new SqlServerProvider().Capabilities.Atomicity);
        Assert.Equal(ApplyAtomicity.Partial, new SqlServerProvider().Capabilities.PlanAtomicity);
    }
}
