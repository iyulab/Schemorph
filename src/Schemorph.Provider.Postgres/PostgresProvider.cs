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
    /// Every capability absent from this list must throw from
    /// <see cref="Refuse"/> — ProviderBoundaryTests pins the symmetry, and the
    /// refusal hint quotes exactly these lines.
    /// </summary>
    internal static readonly string[] DeclaredCapabilities = { "inspect" };

    public string Name => ProviderName;

    /// <summary>
    /// Slice P0 declares only reading. Atomicity stays undeclared: this
    /// provider has no apply, so it must not claim what an apply would
    /// guarantee — P1 declares `transactional` together with the apply it
    /// earns it with (tool-owned transaction, ADR-0007).
    /// </summary>
    public ProviderCapabilities Capabilities { get; } = new(
        DeclaredCapabilities, Atomicity: null);

    public async Task<InspectResult> InspectAsync(InspectRequest request, CancellationToken cancellationToken = default)
    {
        var tables = await CatalogReader.ReadTablesAsync(
            request.ConnectionString, TargetSchemaOf(request.ConnectionString),
            cancellationToken: cancellationToken);
        return new InspectResult(DesiredStateRenderer.Render(tables));
    }

    /// <summary>
    /// The schema to read. InspectRequest carries only a connection string (a core
    /// record this slice does not change), so the target comes from the connection's
    /// own search path — the same place a psql session would take it from. Reading
    /// several schemas in one pass would need a new field on the core request, which
    /// belongs to a later slice.
    ///
    /// Resolution follows the engine's own rules for a search_path entry:
    /// <c>$user</c> — quoted or not, because the server default is literally
    /// <c>"$user", public</c> — means the connection's user name; unquoted names
    /// fold to lower case; quoted names are taken verbatim (with <c>""</c>
    /// unescaped); empty entries are skipped. Purely lexical on purpose: whether
    /// the schema actually exists is the reader's business, not this function's.
    /// </summary>
    internal static string TargetSchemaOf(string connectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        if (string.IsNullOrWhiteSpace(builder.SearchPath)) return "public";

        foreach (var raw in builder.SearchPath.Split(','))
        {
            var entry = raw.Trim();
            if (entry.Length == 0) continue;

            var quoted = entry.Length >= 2 && entry.StartsWith('"') && entry.EndsWith('"');
            var name = quoted
                ? entry[1..^1].Replace("\"\"", "\"", StringComparison.Ordinal)
                : entry.ToLowerInvariant();
            if (name.Length == 0) continue;

            if (name == "$user")
            {
                if (!string.IsNullOrWhiteSpace(builder.Username)) return builder.Username;
                continue;   // nothing to resolve it to — the engine would skip a missing schema too
            }

            return name;
        }

        return "public";
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
        => new(ProviderName, capability, string.Join(", ", DeclaredCapabilities));
}
