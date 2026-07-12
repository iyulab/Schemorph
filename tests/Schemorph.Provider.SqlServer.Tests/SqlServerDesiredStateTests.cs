using Schemorph.Core.Providers;

namespace Schemorph.Provider.SqlServer.Tests;

/// <summary>
/// The IDesiredState boundary contract, made loud: a state loaded by another
/// provider, or one whose load failed, must never flow into compare/apply/
/// analyze silently — the core checks Errors first, and the provider throws
/// if a bad handle slips through anyway.
/// </summary>
public sealed class SqlServerDesiredStateTests : IDisposable
{
    private readonly string _dir = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), $"schemorph-state-{Guid.NewGuid():N}")).FullName;
    private readonly SqlServerProvider _provider = new();

    private sealed class ForeignState : IDesiredState
    {
        public IReadOnlyList<RawMessage> Warnings => Array.Empty<RawMessage>();
        public IReadOnlyList<RawMessage> Errors => Array.Empty<RawMessage>();
    }

    [Fact]
    public async Task A_state_loaded_by_another_provider_is_rejected()
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => _provider.AnalyzeProgrammablesAsync(new ForeignState()));
    }

    [Fact]
    public async Task A_state_with_load_errors_is_rejected_not_processed()
    {
        File.WriteAllText(Path.Combine(_dir, "broken.sql"), "CREATE TABEL dbo.X (Id INT);");
        var state = await _provider.LoadDesiredStateAsync(_dir);
        Assert.NotEmpty(state.Errors);

        await Assert.ThrowsAsync<ArgumentException>(
            () => _provider.AnalyzeProgrammablesAsync(state));
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);
}
