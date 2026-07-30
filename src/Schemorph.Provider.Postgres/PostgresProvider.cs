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
    /// What this provider can do today: the table core — reading,
    /// and diff/apply over tables, columns, constraints and the target
    /// schema itself. Every capability absent from this list must throw from
    /// <see cref="Refuse"/> — ProviderBoundaryTests pins the symmetry, and the
    /// refusal hint quotes exactly these lines.
    /// </summary>
    internal static readonly string[] DeclaredCapabilities =
        { "inspect", "tables", "columns", "constraints", "schemas" };

    public string Name => ProviderName;

    /// <summary>
    /// This provider earns `transactional` (ADR-0007, ADR-0004 addendum): the
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
        var compared = await CompareCoreAsync(request.DesiredState, request.ConnectionString, cancellationToken);
        return new CompareResult(compared.Comparison.Changes, compared.Messages,
            compared.UpdateScript, compared.ChangeScripts);
    }

    public async Task<ApplyResult> ApplyAsync(
        ApplyRequest request,
        Func<RawChange, bool> includeChange,
        Action<CompareResult>? onChangesComputed = null,
        CancellationToken cancellationToken = default)
    {
        // Same-snapshot invariant: the comparison announced through the hook is
        // the one this apply executes — no second comparison, no diff-apply race.
        var compared =
            await CompareCoreAsync(request.DesiredState, request.ConnectionString, cancellationToken);

        // A comparison this provider cannot carry out never becomes an announced
        // plan: the hook is what the fingerprint gate and the caller's rendering
        // are built from, so failing here keeps both from existing at all.
        if (compared.Messages.Any(m => m.Severity == "Error"))
        {
            return new ApplyResult(false, Array.Empty<RawChange>(), Array.Empty<RawChange>(), compared.Messages);
        }

        // The same script AND the same attribution diff advertised: the fingerprint
        // binds both (plan format 1.5), so anything the gate hashes has to be
        // produced identically on this path — the asymmetry that broke the gate once.
        onChangesComputed?.Invoke(new CompareResult(
            compared.Comparison.Changes, Array.Empty<RawMessage>(),
            compared.UpdateScript, compared.ChangeScripts));

        var included = compared.Comparison.Changes.Where(includeChange).ToList();
        var excluded = compared.Comparison.Changes.Where(c => !includeChange(c)).ToList();

        // Exclusions are masked BEFORE synthesis: an excluded drop keeps its
        // table out of the script entirely, rather than being filtered out of
        // statements after the fact.
        var (desired, live) = MaskExclusions(compared.Snapshots.Desired, compared.Snapshots.Live, excluded);
        var statements = DdlSynthesizer.Synthesize(TargetSchemaOf(request.ConnectionString), desired, live);

        // Masking removes work, so it can also remove the statement an included
        // change needed — the pairing has to be re-checked against what will
        // actually run, not only against the unmasked comparison above.
        if (SynthesisGap(included, statements) is { } gap)
        {
            return new ApplyResult(false, Array.Empty<RawChange>(), excluded, new[] { gap });
        }

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
    /// into a desired state (it refuses them at the door), so
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
    /// One comparison pass and everything derived from it — including the
    /// messages that decide whether it may become a plan at all.
    /// </summary>
    private sealed record Compared(
        SnapshotComparer.Comparison Comparison,
        Snapshots Snapshots,
        string? UpdateScript,
        IReadOnlyList<ChangeScript> ChangeScripts,
        IReadOnlyList<RawMessage> Messages);

    /// <summary>
    /// The shadow pipeline (ADR-0007): desired state applied to a scratch
    /// schema, both sides read back in comparison mode, compared structurally.
    /// An index difference refuses, because a plan that cannot see a difference
    /// must not claim a sync.
    /// </summary>
    private async Task<Compared> CompareCoreAsync(
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
            throw Refuse($"index changes ({string.Join("; ", comparison.OutOfScope)})");
        }

        var statements = DdlSynthesizer.Synthesize(schema, desired, live);
        var updateScript = statements.Count == 0 ? null : ComposeScript(schema, statements);

        // Synthesis is what executes here, so an unsynthesized change is not a
        // missing explanation — it is a change that cannot happen. Reported as an
        // Error on the comparison, which fails every verb that reads it rather
        // than handing anyone a plan the provider cannot carry out.
        var messages = SynthesisGap(comparison.Changes, statements) is { } gap
            ? new[] { gap }
            : Array.Empty<RawMessage>();

        return new Compared(comparison, new Snapshots(desired, live), updateScript,
            AttributeStatements(statements, desired, live), messages);
    }

    /// <summary>
    /// The per-change slices the plan carries to explain itself — descriptive only:
    /// what executes is always the whole update script. Attribution is exact here
    /// rather than inferred, because synthesis records the table each statement
    /// belongs to as it emits it; there is no script to parse back.
    ///
    /// <c>Rebuild</c> is always false: this provider alters in place and has no
    /// table-rebuild path, so claiming one would warn about a cost nobody pays.
    /// </summary>
    private static IReadOnlyList<ChangeScript> AttributeStatements(
        IReadOnlyList<DdlSynthesizer.Statement> statements,
        IReadOnlyList<PgTable> desired, IReadOnlyList<PgTable> live)
    {
        var desiredByName = desired.ToDictionary(t => t.Name, StringComparer.Ordinal);
        var liveByName = live.ToDictionary(t => t.Name, StringComparer.Ordinal);

        return statements
            .GroupBy(s => s.ObjectName, StringComparer.Ordinal)
            .Select(g => new ChangeScript(
                g.Key,
                string.Join("\n", g.Select(s => s.Sql)),
                Rebuild: false,
                AddsNotNullWithoutDefault: AddsNotNullWithoutDefault(
                    desiredByName.GetValueOrDefault(g.Key),
                    liveByName.GetValueOrDefault(g.Key)),
                RecreatesColumn: RecreatesColumn(
                    desiredByName.GetValueOrDefault(g.Key),
                    liveByName.GetValueOrDefault(g.Key))))
            .ToList();
    }

    /// <summary>
    /// Whether this table has a column that must be dropped and added back rather
    /// than altered — the hazard <c>SCHEMORPH107</c> exists for: the table survives
    /// the apply and that one column's values do not, under a plan entry that reads
    /// as an ordinary in-place alter.
    ///
    /// One shape reaches this today: a column that gains a generation expression or
    /// changes the one it has, which has no in-place form on the supported baseline.
    /// The reverse — losing an expression — is performed in place and keeps every
    /// value, so it is deliberately not reported here.
    ///
    /// Judged on the model rather than on the emitted text, so it is proven rather
    /// than pattern-matched, and only for a column that exists on both sides: a
    /// generated column in a brand-new table replaces nothing.
    /// </summary>
    private static bool RecreatesColumn(PgTable? want, PgTable? have)
    {
        if (want is null || have is null) return false;

        var live = have.Columns.ToDictionary(c => c.Name, StringComparer.Ordinal);

        return want.Columns.Any(c =>
            live.TryGetValue(c.Name, out var existing)
            && c.GeneratedAs != existing.GeneratedAs
            && c.GeneratedAs is not null);
    }

    /// <summary>
    /// Whether this table gains a NOT NULL column with nothing to fill it — the
    /// hazard <c>SCHEMORPH101</c> exists for: the statement fails outright on a
    /// table that already holds rows. Judged on the model rather than on the
    /// emitted text, so it is proven, not pattern-matched.
    ///
    /// Only for a table that already exists: the same column in a fresh
    /// <c>CREATE TABLE</c> is harmless, because there are no rows to violate it.
    /// Identity and generated columns are excluded — the engine supplies their
    /// values, so NOT NULL without a default is not a gap there.
    /// </summary>
    private static bool AddsNotNullWithoutDefault(PgTable? want, PgTable? have)
    {
        if (want is null || have is null) return false;

        var existing = have.Columns.Select(c => c.Name).ToHashSet(StringComparer.Ordinal);

        return want.Columns.Any(c =>
            !existing.Contains(c.Name)
            && c.NotNull
            && c.Default is null
            && c.Identity == PgIdentity.None
            && c.GeneratedAs is null);
    }

    /// <summary>
    /// The provider checking its own work: every change the comparison reported
    /// must be carried by at least one synthesized statement. The two halves reach
    /// their answers independently — the comparison over structural equality, the
    /// synthesizer over per-member differences — so a disagreement is possible in
    /// principle and must never be silent. Where it happens the apply would run
    /// nothing and still report the change as done, writing a success row into the
    /// audit trail for work that never happened; the plan would then come back
    /// unchanged on the next diff, forever.
    /// </summary>
    internal static RawMessage? SynthesisGap(
        IReadOnlyList<RawChange> changes, IReadOnlyList<DdlSynthesizer.Statement> statements)
    {
        if (changes.Count == 0) return null;

        var carried = statements.Select(s => s.ObjectName).ToHashSet(StringComparer.Ordinal);
        var uncarried = changes
            .Where(c => !carried.Contains(c.ObjectName))
            .Select(c => $"{c.Operation} {c.ObjectType} {c.ObjectName}")
            .ToList();

        return uncarried.Count == 0
            ? null
            : new RawMessage("Error", "SCHEMORPH009",
                $"The comparison reported {uncarried.Count} change(s) that synthesis produced no " +
                $"statement for ({string.Join(", ", uncarried)}). Nothing was applied. This is a " +
                "disagreement inside the provider, not a fault in the desired state — please report it.");
    }

    /// <summary>
    /// The executable form — also what `diff` reports as the update script, so
    /// the reviewed text and the executed text are one artifact. SET LOCAL:
    /// embedded expression texts are unqualified (comparison-mode snapshots),
    /// and the setting must not outlive the transaction that needs it.
    /// </summary>
    private static string ComposeScript(
        string schema, IReadOnlyList<DdlSynthesizer.Statement> statements) =>
        $"CREATE SCHEMA IF NOT EXISTS {DesiredStateRenderer.Quote(schema)};\n" +
        $"SET LOCAL search_path TO {DesiredStateRenderer.Quote(schema)};\n" +
        string.Join("\n", statements.Select(s => s.Sql));

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
