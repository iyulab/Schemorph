using Schemorph.Core.Ledger;
using Schemorph.Core.Providers;

namespace Schemorph.Core.Tests;

/// <summary>Records executed scripts; every other provider capability is out of scope for core tests.</summary>
internal sealed class FakeProvider : IDatabaseProvider
{
    public List<string> ExecutedScripts { get; } = new();
    public List<(string Script, IReadOnlyList<LedgerEntry> Entries)> AtomicExecutions { get; } = new();

    /// <summary>Entries passed to the atomic overload land here, mirroring the real
    /// same-transaction commit (they become visible to subsequent reads).</summary>
    public FakeLedger? Ledger { get; init; }

    /// <summary>Scripts containing this marker throw, simulating a failing statement.</summary>
    public string? FailOnScriptContaining { get; init; }

    public string Name => "fake";

    public Task ExecuteScriptAsync(string connectionString, string script, CancellationToken ct = default)
        => ExecuteScriptAsync(connectionString, script, Array.Empty<LedgerEntry>(), ct);

    public Task ExecuteScriptAsync(string connectionString, string script, IReadOnlyList<LedgerEntry> ledgerEntries, CancellationToken ct = default)
    {
        if (FailOnScriptContaining is not null && script.Contains(FailOnScriptContaining))
        {
            throw new InvalidOperationException($"boom: {script}");
        }
        ExecutedScripts.Add(script);
        AtomicExecutions.Add((script, ledgerEntries));
        Ledger?.Entries.AddRange(ledgerEntries);
        return Task.CompletedTask;
    }

    public Task<InspectResult> InspectAsync(InspectRequest request, CancellationToken ct = default)
        => throw new NotSupportedException();

    public Task<CompareResult> CompareAsync(CompareRequest request, CancellationToken ct = default)
        => throw new NotSupportedException();

    public Task<ApplyResult> ApplyAsync(ApplyRequest request, Func<RawChange, bool> include, Action<IReadOnlyList<RawChange>>? onChangesComputed = null, CancellationToken ct = default)
        => throw new NotSupportedException();

    public Task<ProgrammableAnalysis> AnalyzeProgrammablesAsync(string desiredStateDirectory, CancellationToken ct = default)
        => throw new NotSupportedException();
}

/// <summary>In-memory ledger; entry order stands in for the persisted insertion order.</summary>
internal sealed class FakeLedger : ILedgerStore
{
    public List<LedgerEntry> Entries { get; } = new();

    /// <summary>Simulates a ledger that cannot be written to (e.g. connection loss).</summary>
    public bool AppendThrows { get; set; }

    public Task EnsureInitializedAsync(string connectionString, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task AppendAsync(string connectionString, IReadOnlyList<LedgerEntry> entries, CancellationToken ct = default)
    {
        if (AppendThrows) throw new InvalidOperationException("ledger unavailable");
        Entries.AddRange(entries);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<LedgerEntry>> ReadAsync(string connectionString, string kind, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<LedgerEntry>>(Entries.Where(e => e.Kind == kind).ToList());
}
