using Npgsql;
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

    public async Task<InspectResult> InspectAsync(InspectRequest request, CancellationToken cancellationToken = default)
    {
        var tables = await CatalogReader.ReadTablesAsync(
            request.ConnectionString, TargetSchemaOf(request.ConnectionString), cancellationToken);
        return new InspectResult(DesiredStateRenderer.Render(tables));
    }

    /// <summary>
    /// The schema to read. InspectRequest carries only a connection string (a core
    /// record this slice does not change), so the target comes from the connection's
    /// own search path — the same place a psql session would take it from. Reading
    /// several schemas in one pass would need a new field on the core request, which
    /// belongs to a later slice.
    /// </summary>
    private static string TargetSchemaOf(string connectionString)
    {
        var searchPath = new NpgsqlConnectionStringBuilder(connectionString).SearchPath;
        if (string.IsNullOrWhiteSpace(searchPath)) return "public";

        var first = searchPath.Split(',')[0].Trim().Trim('"');
        return first.Length == 0 ? "public" : first;
    }

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
