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

    /// <summary>Overridable so operation tests can exercise the declared atomicity flowing into plans.</summary>
    public ProviderCapabilities Capabilities { get; init; } =
        new(new[] { "fake" }, ApplyAtomicity.Partial);

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

    /// <summary>Lint signals returned for every script (default: none).</summary>
    public List<MigrationLintSignal> LintSignals { get; } = new();

    public Task<IReadOnlyList<MigrationLintSignal>> LintMigrationScriptAsync(string scriptText, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<MigrationLintSignal>>(LintSignals);

    public Task<InspectResult> InspectAsync(InspectRequest request, CancellationToken ct = default)
        => throw new NotSupportedException();

    // The three below are seeded only by tests that drive a whole operation
    // (ApplyOperation); left unseeded they keep the narrower contract the
    // strategy-level tests rely on — an unexpected call is a test bug, not a
    // silent empty result.

    /// <summary>Returned by <see cref="LoadDesiredStateAsync"/> when set.</summary>
    public IDesiredState? DesiredState { get; init; }

    /// <summary>Returned by <see cref="AnalyzeProgrammablesAsync"/> when set.</summary>
    public ProgrammableAnalysis? Programmables { get; init; }

    /// <summary>Returned by <see cref="ApplyAsync"/> when set, after announcing <see cref="Computed"/>.</summary>
    public ApplyResult? ApplyOutcome { get; init; }

    /// <summary>The comparison <see cref="ApplyAsync"/> announces through its hook.</summary>
    public CompareResult? Computed { get; init; }

    public Task<IDesiredState> LoadDesiredStateAsync(string desiredStateDirectory, CancellationToken ct = default)
        => DesiredState is null ? throw new NotSupportedException() : Task.FromResult(DesiredState);

    public Task<CompareResult> CompareAsync(CompareRequest request, CancellationToken ct = default)
        => throw new NotSupportedException();

    public Task<ApplyResult> ApplyAsync(ApplyRequest request, Func<RawChange, bool> include, Action<CompareResult>? onChangesComputed = null, CancellationToken ct = default)
    {
        if (ApplyOutcome is null) throw new NotSupportedException();
        if (Computed is not null) onChangesComputed?.Invoke(Computed);
        return Task.FromResult(ApplyOutcome);
    }

    public Task<ProgrammableAnalysis> AnalyzeProgrammablesAsync(IDesiredState desiredState, CancellationToken ct = default)
        => Programmables is null ? throw new NotSupportedException() : Task.FromResult(Programmables);

    /// <summary>Object names whose live definition "matches" the file (brownfield reconciliation).</summary>
    public HashSet<string> MatchingLiveObjects { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every object set the runner asked to match, for asserting when live lookups happen.</summary>
    public List<IReadOnlyList<string>> LiveMatchQueries { get; } = new();

    public Task<IReadOnlyList<ProgrammableObjectInfo>> FilterMatchingLiveDefinitionsAsync(
        string connectionString, IReadOnlyList<ProgrammableObjectInfo> objects, CancellationToken ct = default)
    {
        LiveMatchQueries.Add(objects.Select(o => o.ObjectName).ToList());
        return Task.FromResult<IReadOnlyList<ProgrammableObjectInfo>>(
            objects.Where(o => MatchingLiveObjects.Contains(o.ObjectName)).ToList());
    }
}

/// <summary>A loaded desired state with nothing to complain about (or the warnings/errors given).</summary>
internal sealed record FakeDesiredState(
    IReadOnlyList<RawMessage>? WarningList = null,
    IReadOnlyList<RawMessage>? ErrorList = null) : IDesiredState
{
    public IReadOnlyList<RawMessage> Warnings => WarningList ?? Array.Empty<RawMessage>();
    public IReadOnlyList<RawMessage> Errors => ErrorList ?? Array.Empty<RawMessage>();
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
