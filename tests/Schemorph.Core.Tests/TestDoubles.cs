using Schemorph.Core.Ledger;
using Schemorph.Core.Providers;

namespace Schemorph.Core.Tests;

/// <summary>Records executed scripts; every other provider capability is out of scope for core tests.</summary>
internal sealed class FakeProvider : IDatabaseProvider
{
    public List<string> ExecutedScripts { get; } = new();

    public string Name => "fake";

    public Task ExecuteScriptAsync(string connectionString, string script, CancellationToken ct = default)
    {
        ExecutedScripts.Add(script);
        return Task.CompletedTask;
    }

    public Task<InspectResult> InspectAsync(InspectRequest request, CancellationToken ct = default)
        => throw new NotSupportedException();

    public Task<CompareResult> CompareAsync(CompareRequest request, CancellationToken ct = default)
        => throw new NotSupportedException();

    public Task<ApplyResult> ApplyAsync(ApplyRequest request, Func<RawChange, bool> include, CancellationToken ct = default)
        => throw new NotSupportedException();

    public Task<ProgrammableAnalysis> AnalyzeProgrammablesAsync(string desiredStateDirectory, CancellationToken ct = default)
        => throw new NotSupportedException();
}

/// <summary>In-memory ledger; entry order stands in for the persisted insertion order.</summary>
internal sealed class FakeLedger : ILedgerStore
{
    public List<LedgerEntry> Entries { get; } = new();

    public Task EnsureInitializedAsync(string connectionString, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task AppendAsync(string connectionString, IReadOnlyList<LedgerEntry> entries, CancellationToken ct = default)
    {
        Entries.AddRange(entries);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<LedgerEntry>> ReadAsync(string connectionString, string kind, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<LedgerEntry>>(Entries.Where(e => e.Kind == kind).ToList());
}
