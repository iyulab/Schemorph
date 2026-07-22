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

    // Walks the whole surface rather than checking capabilities one at a time.
    // Per-member tests are what let schemorph_diff and schemorph_inspect ship
    // with no error handling at all (cycle 73) — a new member added without a
    // refusal must fail here, not be discovered by a user. P1 moved load,
    // compare, apply and programmable analysis off this list; what remains is
    // the P3/P4 machinery (redefine script execution, migrations).
    public static TheoryData<string, Func<PostgresProvider, Task>> UndeclaredCapabilities => new()
    {
        { "script execution", p => p.ExecuteScriptAsync("any", "SELECT 1") },
        { "script execution with ledger", p => p.ExecuteScriptAsync("any", "SELECT 1", []) },
        { "live-definition matching", p => p.FilterMatchingLiveDefinitionsAsync("any", []) },
        { "migration lint", p => p.LintMigrationScriptAsync("SELECT 1") },
    };

    [Theory]
    [MemberData(nameof(UndeclaredCapabilities))]
    public async Task Undeclared_capabilities_refuse_with_the_machine_contract(
        string capability, Func<PostgresProvider, Task> invoke)
    {
        var ex = await Assert.ThrowsAsync<UnsupportedByProviderException>(() => invoke(Provider));

        Assert.Equal("postgres", ex.ProviderName);
        Assert.Equal(capability, ex.Capability);
        Assert.Equal("not_implemented", ex.ToError().Code);
        Assert.Equal("unsupported", ex.ToError().Kind);
    }

    [Fact]
    public async Task The_declared_surface_is_what_the_refusals_point_at()
    {
        // The declaration and the refusals pin each other: adding a capability
        // without moving it out of the refusal list breaks this.
        var ex = await Assert.ThrowsAsync<UnsupportedByProviderException>(
            () => Provider.ExecuteScriptAsync("any", "SELECT 1"));

        foreach (var declared in Provider.Capabilities.Declared)
        {
            Assert.Contains(declared, ex.ToError().Hint);
        }
    }

    [Fact]
    public void P1_declares_the_table_core_and_earns_transactional()
    {
        // Slice P1: the table core, and with it the apply guarantee — earned
        // by the tool-owned transaction (ADR-0007, ADR-0004 addendum), not
        // asserted. P0 declared `inspect` alone with NO atomicity, because a
        // provider without an apply must not claim what one would guarantee.
        Assert.Equal(
            new[] { "inspect", "tables", "columns", "constraints", "schemas" },
            Provider.Capabilities.Declared);
        Assert.Equal(ApplyAtomicity.Transactional, Provider.Capabilities.Atomicity);
        Assert.Equal(ApplyAtomicity.Transactional, Provider.Capabilities.PlanAtomicity);
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
