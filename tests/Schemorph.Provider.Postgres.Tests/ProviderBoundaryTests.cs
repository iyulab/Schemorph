using Schemorph.Core.Providers;

namespace Schemorph.Provider.Postgres.Tests;

public class ProviderBoundaryTests
{
    private static readonly PostgresProvider Provider = new();

    [Fact]
    public void The_provider_names_itself_stably()
    {
        Assert.Equal("postgres", Provider.Name);
    }

    /// <summary>
    /// D2: every line this provider declares has to be a real word in the
    /// vocabulary a consumer reads from the CLI manifest — a declared capability
    /// the vocabulary does not know would make "not supported" and "not a
    /// capability this tool models" indistinguishable again, from the other
    /// direction.
    /// </summary>
    [Fact]
    public void Every_declared_capability_is_a_word_in_the_vocabulary()
    {
        Assert.All(Provider.Capabilities.Declared, c => Assert.Contains(c, CapabilityVocabulary.All));
    }

    [Fact]
    public void The_declared_surface_is_spelled_out_and_earns_transactional()
    {
        // The declaration is the promise, so it is spelled out here rather than
        // derived: a capability appears on this line in the same change that
        // makes it real. The apply guarantee is earned by the tool-owned
        // transaction (ADR-0007, ADR-0004 addendum), not asserted — the
        // read-only scope declared `inspect` alone with NO atomicity, because a
        // provider without an apply must not claim what one would guarantee.
        Assert.Equal(
            new[]
            {
                "inspect", "tables", "columns", "constraints", "indexes", "schemas",
                "views", "functions", "triggers", "procedures", "migrations",
            },
            Provider.Capabilities.Declared);
        Assert.Equal(ApplyAtomicity.Transactional, Provider.Capabilities.Atomicity);
        Assert.Equal(ApplyAtomicity.Transactional, Provider.Capabilities.PlanAtomicity);
    }

    [Fact]
    public void The_declared_surface_reaches_full_parity_with_the_vocabulary()
    {
        // P4 (migrations) is the last line: this provider's declared surface
        // now covers the same range as the full vocabulary — Phase 4's own
        // completion criterion for capability range (order-independent; parity
        // means equal range, not equal limitations — see docs/limitations.md).
        Assert.Equivalent(CapabilityVocabulary.All, Provider.Capabilities.Declared, strict: true);
    }

    [Fact]
    public async Task A_foreign_desired_state_is_rejected_with_a_real_error()
    {
        // Load/compare/apply are implemented now, so the guard that matters is
        // the SQL Server provider's own: a desired state loaded elsewhere is an
        // argument error, not a refusal.
        await Assert.ThrowsAsync<ArgumentException>(
            () => Provider.CompareAsync(new CompareRequest(new StubDesiredState(), "any")));
    }

    private sealed class StubDesiredState : IDesiredState
    {
        public IReadOnlyList<RawMessage> Warnings => [];
        public IReadOnlyList<RawMessage> Errors => [];
    }
}
