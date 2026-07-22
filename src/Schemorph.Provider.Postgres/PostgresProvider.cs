using Npgsql;
using Schemorph.Core.Ledger;
using Schemorph.Core.Providers;
using Schemorph.Provider.Postgres.Shadow;

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
    /// What this provider can do today. Slice P1: the table core — reading,
    /// and diff/apply over tables, columns, constraints and the target
    /// schema itself. Every capability absent from this list must throw from
    /// <see cref="Refuse"/> — ProviderBoundaryTests pins the symmetry, and the
    /// refusal hint quotes exactly these lines.
    /// </summary>
    internal static readonly string[] DeclaredCapabilities =
        { "inspect", "tables", "columns", "constraints", "schemas" };

    public string Name => ProviderName;

    /// <summary>
    /// Slice P1 earns `transactional` (ADR-0007, ADR-0004 addendum): the
    /// declarative apply is one tool-owned transaction — the tool holds the
    /// boundary, it does not merely observe a rollback.
    /// </summary>
    public ProviderCapabilities Capabilities { get; } = new(
        DeclaredCapabilities, ApplyAtomicity.Transactional);

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
        => Task.Run<IDesiredState>(() => PgDesiredState.Load(desiredStateDirectory), cancellationToken);

    public async Task<CompareResult> CompareAsync(CompareRequest request, CancellationToken cancellationToken = default)
    {
        var (comparison, _, updateScript) = await CompareCoreAsync(request.DesiredState, request.ConnectionString, cancellationToken);
        return new CompareResult(comparison.Changes, Array.Empty<RawMessage>(), updateScript);
    }

    public async Task<ApplyResult> ApplyAsync(
        ApplyRequest request,
        Func<RawChange, bool> includeChange,
        Action<CompareResult>? onChangesComputed = null,
        CancellationToken cancellationToken = default)
    {
        // Same-snapshot invariant: the comparison announced through the hook is
        // the one this apply executes — no second comparison, no diff-apply race.
        var (comparison, snapshots, updateScript) =
            await CompareCoreAsync(request.DesiredState, request.ConnectionString, cancellationToken);
        onChangesComputed?.Invoke(new CompareResult(comparison.Changes, Array.Empty<RawMessage>(), updateScript));

        var included = comparison.Changes.Where(includeChange).ToList();
        var excluded = comparison.Changes.Where(c => !includeChange(c)).ToList();

        // Exclusions are masked BEFORE synthesis: an excluded drop keeps its
        // table out of the script entirely, rather than being filtered out of
        // statements after the fact.
        var (desired, live) = MaskExclusions(snapshots.Desired, snapshots.Live, excluded);
        var statements = DdlSynthesizer.Synthesize(TargetSchemaOf(request.ConnectionString), desired, live);

        if (statements.Count > 0)
        {
            try
            {
                await PgScriptExecutor.ExecuteAsync(
                    request.ConnectionString,
                    ComposeScript(TargetSchemaOf(request.ConnectionString), statements),
                    Array.Empty<LedgerEntry>(),
                    cancellationToken);
            }
            catch (PostgresException ex)
            {
                return new ApplyResult(false, Array.Empty<RawChange>(), excluded,
                    new[] { new RawMessage("Error", ex.SqlState, ex.MessageText) });
            }
        }

        return new ApplyResult(true, included, excluded, Array.Empty<RawMessage>());
    }

    public Task ExecuteScriptAsync(string connectionString, string script, CancellationToken cancellationToken = default)
        => throw Refuse("script execution");

    public Task ExecuteScriptAsync(string connectionString, string script, IReadOnlyList<LedgerEntry> ledgerEntries, CancellationToken cancellationToken = default)
        => throw Refuse("script execution with ledger");

    /// <summary>
    /// Honestly empty, not refused: the loader admits no programmable files
    /// into a desired state (it refuses them at the door, naming slice P3), so
    /// the analysis of what it loaded is a real answer — zero objects.
    /// </summary>
    public Task<ProgrammableAnalysis> AnalyzeProgrammablesAsync(IDesiredState desiredState, CancellationToken cancellationToken = default)
    {
        PgDesiredState.From(desiredState);   // the guard, not the data
        return Task.FromResult(new ProgrammableAnalysis(
            Array.Empty<ProgrammableObjectInfo>(), Array.Empty<RawMessage>()));
    }

    // ------------------------------------------------------------- pipeline

    private sealed record Snapshots(IReadOnlyList<PgTable> Desired, IReadOnlyList<PgTable> Live);

    /// <summary>
    /// The shadow pipeline (ADR-0007): desired state applied to a scratch
    /// schema, both sides read back in comparison mode, compared structurally.
    /// An index difference refuses — slice P2 — because a plan that cannot see
    /// a difference must not claim a sync (§2 of the dev plan).
    /// </summary>
    private async Task<(SnapshotComparer.Comparison, Snapshots, string?)> CompareCoreAsync(
        IDesiredState desiredState, string connectionString, CancellationToken cancellationToken)
    {
        var state = PgDesiredState.From(desiredState);
        var schema = TargetSchemaOf(connectionString);

        IReadOnlyList<PgTable> desired;
        await using (var shadow = await ShadowSchema.CreateAsync(connectionString, cancellationToken))
        {
            await shadow.ApplyAsync(state.ModelTexts, sourceSchema: schema, cancellationToken);
            desired = await CatalogReader.ReadTablesAsync(
                connectionString, shadow.Name, normalizeSameSchemaReferences: true, cancellationToken);
        }
        var live = await CatalogReader.ReadTablesAsync(
            connectionString, schema, normalizeSameSchemaReferences: true, cancellationToken);

        var comparison = SnapshotComparer.Compare(desired, live);
        if (comparison.OutOfScope.Count > 0)
        {
            throw Refuse($"index changes ({string.Join("; ", comparison.OutOfScope)} — slice P2)");
        }

        var statements = DdlSynthesizer.Synthesize(schema, desired, live);
        var updateScript = statements.Count == 0 ? null : ComposeScript(schema, statements);

        return (comparison, new Snapshots(desired, live), updateScript);
    }

    /// <summary>
    /// The executable form — also what `diff` reports as the update script, so
    /// the reviewed text and the executed text are one artifact. SET LOCAL:
    /// embedded expression texts are unqualified (comparison-mode snapshots),
    /// and the setting must not outlive the transaction that needs it.
    /// </summary>
    private static string ComposeScript(string schema, IReadOnlyList<string> statements) =>
        $"CREATE SCHEMA IF NOT EXISTS {DesiredStateRenderer.Quote(schema)};\n" +
        $"SET LOCAL search_path TO {DesiredStateRenderer.Quote(schema)};\n" +
        string.Join("\n", statements);

    private static (IReadOnlyList<PgTable> Desired, IReadOnlyList<PgTable> Live) MaskExclusions(
        IReadOnlyList<PgTable> desired, IReadOnlyList<PgTable> live, IReadOnlyList<RawChange> excluded)
    {
        if (excluded.Count == 0) return (desired, live);

        var excludedNames = excluded.Select(c => c.ObjectName).ToHashSet(StringComparer.Ordinal);
        var liveByName = live.ToDictionary(t => t.Name, StringComparer.Ordinal);

        // An excluded Add vanishes from the desired side; an excluded Delete
        // vanishes from the live side; an excluded Change keeps the live shape
        // on the desired side. In every case: no statement is synthesized.
        var maskedDesired = desired
            .Where(t => !(excludedNames.Contains(t.Name) && !liveByName.ContainsKey(t.Name)))
            .Select(t => excludedNames.Contains(t.Name) ? liveByName[t.Name] : t)
            .ToList();
        var maskedLive = live
            .Where(t => !excludedNames.Contains(t.Name) || maskedDesired.Any(d => d.Name == t.Name))
            .ToList();

        return (maskedDesired, maskedLive);
    }

    public Task<IReadOnlyList<ProgrammableObjectInfo>> FilterMatchingLiveDefinitionsAsync(
        string connectionString, IReadOnlyList<ProgrammableObjectInfo> objects, CancellationToken cancellationToken = default)
        => throw Refuse("live-definition matching");

    public Task<IReadOnlyList<MigrationLintSignal>> LintMigrationScriptAsync(
        string scriptText, CancellationToken cancellationToken = default)
        => throw Refuse("migration lint");

    private static UnsupportedByProviderException Refuse(string capability)
        => new(ProviderName, capability, string.Join(", ", DeclaredCapabilities));
}
