using Npgsql;
using PgSqlParser;
using Schemorph.Core.Planning;
using Schemorph.Core.Providers;
using Schemorph.Provider.Postgres.Shadow;

namespace Schemorph.Provider.Postgres;

/// <summary>
/// The DROP+CREATE fallback for view redefinition that
/// <see cref="PgProgrammables.ViewRedefineRiskNote"/> left as a known gap:
/// <c>CREATE OR REPLACE VIEW</c> can only append output columns, so a rename,
/// reorder, retype, or removal fails at apply time (SQLSTATE 42P16) even
/// though the plan reports the statement itself.
///
/// The comparison this needs — is the live view's column list a strict prefix
/// of what the desired-state file would produce? — cannot be answered by
/// re-parsing text (ADR-0007: the comparison layer holds no expression
/// semantics; the engine is the normalizer). So both sides come from the
/// engine: the live side from <c>pg_attribute</c>, the desired side from
/// actually creating the file's query under a throwaway name and reading it
/// back the same way. The desired side runs inside the same shadow-schema
/// mechanism <see cref="Shadow.ShadowSchema"/> already gives table diffing
/// (ADR-0007) rather than against the live schema directly — at `diff` time
/// the live tables may not yet carry a column the desired view's query
/// depends on, since nothing has been applied yet; only the shadow already
/// has the desired shape. Whether anything depends on the live view is asked
/// the engine's way too: a real <c>DROP ... RESTRICT</c> against the live
/// schema, rolled back either way — reusing PostgreSQL's own dependency graph
/// instead of re-deriving one from <c>pg_depend</c> by hand.
/// </summary>
internal static class ViewRedefinePlanner
{
    /// <summary>PostgreSQL SQLSTATE for "cannot drop ... because other objects depend on it".</summary>
    private const string DependentObjectsStillExist = "2BP01";

    internal const string DropRecreateRiskNote =
        "The file's column list changed in a way CREATE OR REPLACE VIEW cannot express " +
        "(rename, reorder, retype, or removal); this plan drops and re-creates the view " +
        "instead. Nothing else references it, but any privileges granted directly on the " +
        "view do not survive a drop and must be re-granted after apply.";

    public static async Task<ProgrammableAnalysis> RefineAsync(
        ProgrammableAnalysis analysis, IReadOnlyList<string> desiredModelTexts,
        string connectionString, string schema, CancellationToken cancellationToken)
    {
        var views = analysis.Objects.Where(o => o.ObjectType == "View").ToList();
        if (views.Count == 0) return analysis;

        await using var liveConnection = new NpgsqlConnection(connectionString);
        await liveConnection.OpenAsync(cancellationToken);
        await SetSearchPathAsync(liveConnection, schema, cancellationToken);

        // Only a view that already exists live needs comparing at all — a
        // brand-new one's "redefinition" is a plain CREATE, unconditionally
        // safe, and paying for a shadow schema to confirm that would be pure
        // overhead on the common case (adding a first view).
        var existingLive = new Dictionary<string, List<(string Name, string Type)>>(StringComparer.Ordinal);
        foreach (var view in views)
        {
            if (await ReadColumnsAsync(liveConnection, schema, view.ObjectName, cancellationToken) is { } live)
            {
                existingLive[view.ObjectName] = live;
            }
        }
        if (existingLive.Count == 0) return analysis;

        await using var shadow = await ShadowSchema.CreateAsync(connectionString, cancellationToken);
        await shadow.ApplyAsync(desiredModelTexts, sourceSchema: schema, cancellationToken);
        await using var shadowConnection = new NpgsqlConnection(connectionString);
        await shadowConnection.OpenAsync(cancellationToken);
        await SetSearchPathAsync(shadowConnection, shadow.Name, cancellationToken);

        var refined = analysis.Objects.ToList();
        var messages = analysis.Messages.ToList();

        foreach (var view in views)
        {
            if (!existingLive.TryGetValue(view.ObjectName, out var live)) continue;

            var desired = await ProbeColumnsAsync(shadowConnection, shadow.Name, view, cancellationToken);
            var index = refined.FindIndex(o => o.ObjectName == view.ObjectName);

            if (IsCompatiblePrefix(desired, live))
            {
                // Provably safe (not merely hoped): the blanket warning every
                // view carried since the risk-classification fix stays only
                // where a real gap was found.
                refined[index] = view with { RiskOverride = null, RiskNote = null };
                continue;
            }

            if (await HasDependentsAsync(liveConnection, schema, view.ObjectName, cancellationToken))
            {
                messages.Add(new RawMessage("Error", "SCHEMORPH010",
                    $"{view.ObjectName}: CREATE OR REPLACE VIEW cannot express this column " +
                    "change (rename, reorder, retype, or removal), and another object depends " +
                    "on this view — automatic DROP+CREATE with CASCADE is not implemented. " +
                    "Drop the dependents yourself (or restructure to avoid the incompatible " +
                    "change), then re-run."));
                continue;
            }

            refined[index] = view with
            {
                ApplyScript = $"DROP VIEW {DesiredStateRenderer.Quote(schema)}.{DesiredStateRenderer.Quote(view.ObjectName)};\n{view.ApplyScript}",
                StatementCount = 2,
                RiskOverride = RiskLevel.Warning,
                RiskNote = DropRecreateRiskNote,
            };
        }

        return new ProgrammableAnalysis(refined, messages);
    }

    private static async Task SetSearchPathAsync(
        NpgsqlConnection connection, string schema, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"SET search_path TO {DesiredStateRenderer.Quote(schema)}", connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Safe iff every live column survives at its same position with the same
    /// name and type — the exact shape <c>CREATE OR REPLACE VIEW</c> allows.
    /// Appending beyond that is fine; anything else (including having fewer
    /// desired columns than live) is not.
    /// </summary>
    private static bool IsCompatiblePrefix(
        IReadOnlyList<(string Name, string Type)> desired, IReadOnlyList<(string Name, string Type)> live)
    {
        if (desired.Count < live.Count) return false;
        for (var i = 0; i < live.Count; i++)
        {
            if (desired[i].Name != live[i].Name || desired[i].Type != live[i].Type) return false;
        }
        return true;
    }

    /// <summary>Null means no live view by this name — a view always has at least one column.</summary>
    private static async Task<List<(string Name, string Type)>?> ReadColumnsAsync(
        NpgsqlConnection connection, string schema, string viewName, CancellationToken cancellationToken)
    {
        var columns = await ReadColumnsInternalAsync(connection, null, schema, viewName, cancellationToken);
        return columns.Count == 0 ? null : columns;
    }

    /// <summary>
    /// Creates the file's exact query under a throwaway name inside the
    /// (disposable) shadow schema the caller already switched this
    /// connection's search_path to, reads its columns back, and rolls the
    /// transaction back — a probe, never a change, and disposing the whole
    /// shadow schema afterward would have erased it anyway.
    /// </summary>
    private static async Task<List<(string Name, string Type)>> ProbeColumnsAsync(
        NpgsqlConnection shadowConnection, string shadowSchema, ProgrammableObjectInfo view,
        CancellationToken cancellationToken)
    {
        var probeName = $"__schemorph_probe_{Guid.NewGuid():N}"[..40];
        var probeSql = RenameViewTarget(view.ApplyScript, probeName);

        await using var transaction = await shadowConnection.BeginTransactionAsync(cancellationToken);
        try
        {
            await using (var create = new NpgsqlCommand(probeSql, shadowConnection, transaction))
            {
                await create.ExecuteNonQueryAsync(cancellationToken);
            }
            return await ReadColumnsInternalAsync(
                shadowConnection, transaction, shadowSchema, probeName, cancellationToken);
        }
        finally
        {
            await transaction.RollbackAsync(cancellationToken);
        }
    }

    private static async Task<bool> HasDependentsAsync(
        NpgsqlConnection liveConnection, string schema, string viewName, CancellationToken cancellationToken)
    {
        await using var transaction = await liveConnection.BeginTransactionAsync(cancellationToken);
        try
        {
            await using var drop = new NpgsqlCommand(
                $"DROP VIEW {DesiredStateRenderer.Quote(schema)}.{DesiredStateRenderer.Quote(viewName)} RESTRICT",
                liveConnection, transaction);
            await drop.ExecuteNonQueryAsync(cancellationToken);
            return false;
        }
        catch (PostgresException ex) when (ex.SqlState == DependentObjectsStillExist)
        {
            return true;
        }
        finally
        {
            await transaction.RollbackAsync(cancellationToken);
        }
    }

    private const string ColumnsSql = """
        SELECT a.attname, format_type(a.atttypid, a.atttypmod)
        FROM pg_attribute a
        JOIN pg_class c ON c.oid = a.attrelid
        JOIN pg_namespace n ON n.oid = c.relnamespace
        WHERE n.nspname = @schema AND c.relname = @name AND c.relkind = 'v'
          AND a.attnum > 0 AND NOT a.attisdropped
        ORDER BY a.attnum
        """;

    private static async Task<List<(string Name, string Type)>> ReadColumnsInternalAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, string schema, string name,
        CancellationToken cancellationToken)
    {
        var result = new List<(string, string)>();
        await using var command = new NpgsqlCommand(ColumnsSql, connection, transaction);
        command.Parameters.AddWithValue("schema", schema);
        command.Parameters.AddWithValue("name", name);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add((reader.GetString(0), reader.GetString(1)));
        }
        return result;
    }

    /// <summary>
    /// Renames only the CREATE VIEW's own target (an AST field write, never
    /// text — the parse-tree-not-text discipline <see cref="Shadow.SchemaRewriter"/>
    /// already uses) so the probe can execute the file's exact query body
    /// under a throwaway name. Every other identifier — including the
    /// unqualified table references the query selects from — is untouched:
    /// this provider's programmable objects are bare-name, single-schema by
    /// convention (<see cref="PgProgrammables"/>), so they already resolve
    /// through whichever schema the probe connection's search_path names.
    /// </summary>
    private static string RenameViewTarget(string createViewSql, string probeName)
    {
        var parsed = Parser.Parse(createViewSql);
        if (parsed.Error is not null || parsed.Value is null
            || parsed.Value.Stmts.Count != 1 || parsed.Value.Stmts[0].Stmt.ViewStmt is not { } view)
        {
            throw new InvalidOperationException(
                "Failed to re-parse a view's own idempotent redefinition script for probing — " +
                "this is an internal inconsistency, please report it.");
        }

        view.View.Relname = probeName;
        view.Replace = false;   // the probe name never pre-exists

        var deparsed = Parser.Deparse(parsed.Value);
        if (deparsed.Error is not null || deparsed.Value is null)
        {
            throw new InvalidOperationException(
                $"Failed to render a probe redefinition of a view ({deparsed.Error?.Message}) — " +
                "this is an internal inconsistency, please report it.");
        }
        return deparsed.Value;
    }
}
