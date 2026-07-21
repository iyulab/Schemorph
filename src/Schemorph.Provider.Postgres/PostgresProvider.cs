using Schemorph.Core.Ledger;
using Schemorph.Core.Providers;

namespace Schemorph.Provider.Postgres;

/// <summary>
/// The PostgreSQL provider (ADR-0007: native pg_catalog comparison). Built in
/// slices, and honest at every one of them: it declares the capabilities it has
/// and refuses the rest, so a partial provider never produces a result it cannot
/// stand behind. <see cref="DeclaredCapabilities"/> grows by one line per slice.
/// </summary>
public sealed class PostgresProvider : IDatabaseProvider
{
    public const string ProviderName = "postgres";

    /// <summary>
    /// What this provider can do today. Slice P0: reading a live database.
    /// Every capability absent from this string must throw from
    /// <see cref="Refuse"/> — ProviderBoundaryTests pins the symmetry.
    /// </summary>
    internal const string DeclaredCapabilities = "inspect";

    public string Name => ProviderName;

    public Task<InspectResult> InspectAsync(InspectRequest request, CancellationToken cancellationToken = default)
        => throw Refuse("inspect");   // Task 4 implements this.

    public Task<IDesiredState> LoadDesiredStateAsync(string desiredStateDirectory, CancellationToken cancellationToken = default)
        => throw Refuse("desired-state loading");

    public Task<CompareResult> CompareAsync(CompareRequest request, CancellationToken cancellationToken = default)
        => throw Refuse("compare");

    public Task<ApplyResult> ApplyAsync(
        ApplyRequest request,
        Func<RawChange, bool> includeChange,
        Action<CompareResult>? onChangesComputed = null,
        CancellationToken cancellationToken = default)
        => throw Refuse("apply");

    public Task ExecuteScriptAsync(string connectionString, string script, CancellationToken cancellationToken = default)
        => throw Refuse("script execution");

    public Task ExecuteScriptAsync(string connectionString, string script, IReadOnlyList<LedgerEntry> ledgerEntries, CancellationToken cancellationToken = default)
        => throw Refuse("script execution with ledger");

    public Task<ProgrammableAnalysis> AnalyzeProgrammablesAsync(IDesiredState desiredState, CancellationToken cancellationToken = default)
        => throw Refuse("programmable analysis");

    public Task<IReadOnlyList<ProgrammableObjectInfo>> FilterMatchingLiveDefinitionsAsync(
        string connectionString, IReadOnlyList<ProgrammableObjectInfo> objects, CancellationToken cancellationToken = default)
        => throw Refuse("live-definition matching");

    public Task<IReadOnlyList<MigrationLintSignal>> LintMigrationScriptAsync(
        string scriptText, CancellationToken cancellationToken = default)
        => throw Refuse("migration lint");

    private static UnsupportedByProviderException Refuse(string capability)
        => new(ProviderName, capability, DeclaredCapabilities);
}
