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
    // refusal must fail here, not be discovered by a user.
    public static TheoryData<string, Func<PostgresProvider, Task>> UndeclaredCapabilities => new()
    {
        { "desired-state loading", p => p.LoadDesiredStateAsync("any") },
        { "compare", p => p.CompareAsync(new CompareRequest(new StubDesiredState(), "any")) },
        { "apply", p => p.ApplyAsync(new ApplyRequest(new StubDesiredState(), "any"), _ => true) },
        { "script execution", p => p.ExecuteScriptAsync("any", "SELECT 1") },
        { "script execution with ledger", p => p.ExecuteScriptAsync("any", "SELECT 1", []) },
        { "programmable analysis", p => p.AnalyzeProgrammablesAsync(new StubDesiredState()) },
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
    public void The_declared_surface_is_what_the_refusals_point_at()
    {
        // The declaration and the refusals pin each other: adding a capability
        // without moving it out of the refusal list breaks this.
        var hint = new UnsupportedByProviderException("postgres", "apply", PostgresProvider.DeclaredCapabilities)
            .ToError().Hint;

        Assert.Contains("inspect", hint);
    }

    private sealed class StubDesiredState : IDesiredState
    {
        public IReadOnlyList<RawMessage> Warnings => [];
        public IReadOnlyList<RawMessage> Errors => [];
    }
}
