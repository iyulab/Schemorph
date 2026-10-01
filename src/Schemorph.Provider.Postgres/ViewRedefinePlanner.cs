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
        await SetSearchPathAsync(liveConnection, cancellationToken, schema);

        var refined = analysis.Objects.ToList();
        var messages = analysis.Messages.ToList();
        var droppedByRedefine = new List<string>();
        IReadOnlyList<PgLiveProgrammables.ViewRead>? reads = null;

        // Only a view that already exists live needs comparing at all — a
        // brand-new one's "redefinition" is a plain CREATE, unconditionally
        // safe (so the blanket warning comes off without a shadow probe), and
        // paying for a shadow schema to confirm that would be pure overhead
        // on the common case (adding a first view).
        var existingLive = new Dictionary<string, List<(string Name, string Type)>>(StringComparer.Ordinal);
        foreach (var view in views)
        {
            if (await ReadColumnsAsync(liveConnection, schema, view.ObjectName, cancellationToken) is { } live)
            {
                existingLive[view.ObjectName] = live;
            }
            else
            {
                refined[refined.FindIndex(o => o.ObjectName == view.ObjectName)] =
                    view with { RiskOverride = null, RiskNote = null };
            }
        }
        if (existingLive.Count == 0) return analysis with { Objects = refined, Messages = messages };

        await using var shadow = await ShadowSchema.CreateAsync(connectionString, cancellationToken);
        await shadow.ApplyAsync(desiredModelTexts, sourceSchema: schema, cancellationToken);
        await using var shadowConnection = new NpgsqlConnection(connectionString);
        await shadowConnection.OpenAsync(cancellationToken);
        // The shadow first, so every object the desired state declares resolves to its desired
        // shape; then the target schema, so a name the desired state does not declare — a function
        // an extension installed there, say — resolves exactly as it will when apply runs (apply's
        // search_path is the target schema alone). Unqualified CREATEs land in the first entry.
        await SetSearchPathAsync(shadowConnection, cancellationToken, shadow.Name, schema);
        await MaterializeAsync(shadowConnection, schema, shadow.Name, analysis.Objects, cancellationToken);

        foreach (var view in views)
        {
            if (!existingLive.TryGetValue(view.ObjectName, out var live)) continue;

            List<(string Name, string Type)> desired;
            try
            {
                desired = await ProbeColumnsAsync(shadowConnection, schema, shadow.Name, view, cancellationToken);
            }
            catch (PostgresException ex)
            {
                // The scratch schema holds the desired state and nothing the target schema does not
                // already offer apply, so a view that cannot be built there is a fault in its file —
                // a typo, a column or function that is not declared. Name the file: the engine's own
                // message says what is missing, never where.
                messages.Add(ProbeFailed(view, PgSqlStateHints.Describe(ex)));
                continue;
            }
            var index = refined.FindIndex(o => o.ObjectName == view.ObjectName);

            if (IsCompatiblePrefix(desired, live))
            {
                // Provably safe (not merely hoped): the blanket warning every
                // view carried since the risk-classification fix stays only
                // where a real gap was found.
                refined[index] = view with { RiskOverride = null, RiskNote = null };
                continue;
            }

            // The views reading this one go with it — dropped ahead of it, re-created after it —
            // when every one of them is the desired state's to re-create.
            reads ??= await PgLiveProgrammables.ReadViewReadsAsync(connectionString, schema, cancellationToken);
            var dependents = ReadersOf(view.ObjectName, reads);
            var declaredNames = views.Select(v => v.ObjectName).ToHashSet(StringComparer.Ordinal);
            var blocked = await FirstUndroppableAsync(
                liveConnection, schema, [.. dependents, view.ObjectName], cancellationToken);
            if (blocked is not null)
            {
                messages.Add(new RawMessage("Error", "SCHEMORPH010",
                    $"{view.ObjectName}: CREATE OR REPLACE VIEW cannot express this column " +
                    "change (rename, reorder, retype, or removal), so the view has to be dropped " +
                    "and created again — " +
                    (blocked == view.ObjectName
                        ? "but an object outside the desired state depends on it"
                        : $"and with it {blocked}, which reads it, but an object outside the desired state depends on {blocked}") +
                    " (another schema's view, a rule, a routine body). Automatic CASCADE is not " +
                    "implemented: drop that object yourself (or restructure to avoid the incompatible " +
                    "change), then re-run."));
                continue;
            }

            // A reader no file declares is the declarative stage's to drop (a Delete in the
            // same plan); only the declared ones are this script's to drop and re-create.
            var declaredDependents = dependents.Where(declaredNames.Contains).ToList();
            var quotedSchema = DesiredStateRenderer.Quote(schema);
            // IF EXISTS throughout: the declarative stage may already have dropped any of
            // them (a view reading what that stage removes goes first — ViewsDroppedFirst).
            var drops = declaredDependents.Append(view.ObjectName)
                .Select(v => $"DROP VIEW IF EXISTS {quotedSchema}.{DesiredStateRenderer.Quote(v)};");
            refined[index] = view with
            {
                ApplyScript = string.Join("\n", drops) + "\n" + view.ApplyScript,
                StatementCount = declaredDependents.Count + 2,
                RiskOverride = RiskLevel.Warning,
                RiskNote = declaredDependents.Count == 0
                    ? DropRecreateRiskNote
                    : DropRecreateWithDependentsRiskNote(declaredDependents),
            };
            droppedByRedefine.AddRange(declaredDependents);
        }

        return analysis with
        {
            Objects = refined,
            Messages = messages,
            DroppedByRedefine = [.. analysis.DroppedByRedefine ?? Array.Empty<string>(), .. droppedByRedefine.Distinct()],
        };
    }

    /// <summary>
    /// Creates every declared view, function and procedure in the shadow under its own name, in
    /// dependency order, so a probe of a view that selects from another view or calls a declared
    /// function resolves it — and resolves it as the desired state defines it, not as it stands
    /// live. The shadow holds only tables otherwise, and such a view could never be probed: once
    /// applied, its desired state could not be compared again. Routine bodies are not checked
    /// here (<c>check_function_bodies</c> off): a probe needs a routine's signature and result
    /// type, not what its body reads. Triggers are left out — nothing a view reads. An object
    /// that cannot be created here is left out too: its own probe, if it needs one, reports why,
    /// and a view that reads it fails its probe as it did before.
    /// </summary>
    private static async Task MaterializeAsync(
        NpgsqlConnection shadowConnection, string sourceSchema, string shadowSchema,
        IReadOnlyList<ProgrammableObjectInfo> objects, CancellationToken cancellationToken)
    {
        var candidates = objects.Where(o => o.ObjectType != "DmlTrigger").ToList();
        if (candidates.Count == 0) return;

        await using (var setting = new NpgsqlCommand("SET check_function_bodies = off", shadowConnection))
        {
            await setting.ExecuteNonQueryAsync(cancellationToken);
        }

        var byName = candidates.ToDictionary(v => v.ObjectName, StringComparer.Ordinal);
        var remaining = candidates.Select(v => v.ObjectName).ToHashSet(StringComparer.Ordinal);
        while (remaining.Count > 0)
        {
            // A view is ready once no view it reads is still waiting; a cycle cannot be
            // created in PostgreSQL, so taking the rest by name only ends the loop.
            var ready = remaining
                .Where(v => !byName[v].DependsOn.Any(remaining.Contains))
                .OrderBy(v => v, StringComparer.Ordinal)
                .ToList();
            if (ready.Count == 0) ready = remaining.OrderBy(v => v, StringComparer.Ordinal).ToList();

            foreach (var name in ready)
            {
                remaining.Remove(name);
                var obj = byName[name];
                var sql = obj.ObjectType == "View"
                    ? RetargetForProbe(obj.ApplyScript, name, sourceSchema, shadowSchema)
                    : RetargetRoutineForShadow(obj.ApplyScript, sourceSchema, shadowSchema);
                try
                {
                    await using var create = new NpgsqlCommand(sql, shadowConnection);
                    await create.ExecuteNonQueryAsync(cancellationToken);
                }
                catch (PostgresException)
                {
                    // See the summary: left out, and whatever needed it reports its own failure.
                }
            }
        }
    }

    private static async Task SetSearchPathAsync(
        NpgsqlConnection connection, CancellationToken cancellationToken, params string[] schemas)
    {
        await using var command = new NpgsqlCommand(
            $"SET search_path TO {string.Join(", ", schemas.Select(DesiredStateRenderer.Quote))}", connection);
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
        NpgsqlConnection shadowConnection, string sourceSchema, string shadowSchema,
        ProgrammableObjectInfo view, CancellationToken cancellationToken)
    {
        var probeName = $"__schemorph_probe_{Guid.NewGuid():N}"[..40];
        var probeSql = RetargetForProbe(view.ApplyScript, probeName, sourceSchema, shadowSchema);

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

    internal static RawMessage ProbeFailed(ProgrammableObjectInfo view, EngineError engine) =>
        new("Error", "SCHEMORPH013",
            $"{view.ObjectName} ({view.FilePath}): the view's file cannot be created against the desired " +
            $"state, so its column change cannot be checked — {engine.Code}: {engine.Text}");

    private static string DropRecreateWithDependentsRiskNote(IReadOnlyList<string> dependents) =>
        "The file's column list changed in a way CREATE OR REPLACE VIEW cannot express " +
        "(rename, reorder, retype, or removal); this plan drops and re-creates the view instead, " +
        $"and drops the views that read it first ({string.Join(", ", dependents)}) — each is " +
        "re-created from its own file after this one. Privileges granted directly on any of them " +
        "do not survive a drop and must be re-granted after apply.";

    /// <summary>
    /// Every live view reading <paramref name="viewName"/>, directly or through another, in the
    /// order they can be dropped — a view that reads another ahead of the one it reads.
    /// </summary>
    private static IReadOnlyList<string> ReadersOf(string viewName, IReadOnlyList<PgLiveProgrammables.ViewRead> reads)
    {
        var readers = new HashSet<string>(StringComparer.Ordinal);
        var frontier = new Queue<string>([viewName]);
        while (frontier.Count > 0)
        {
            var source = frontier.Dequeue();
            foreach (var read in reads.Where(r => r.Source == source && r.View != viewName))
            {
                if (readers.Add(read.View)) frontier.Enqueue(read.View);
            }
        }
        var edges = reads.Where(r => readers.Contains(r.View) && readers.Contains(r.Source))
            .Select(r => (r.View, r.Source)).Distinct().ToList();
        return PgLiveProgrammables.ReadersFirst(readers, edges).ToList();
    }

    /// <summary>
    /// Asks the engine whether the given views can be dropped, in order, with nothing else
    /// going with them — a real <c>DROP ... RESTRICT</c> of each against the live schema,
    /// rolled back either way, so PostgreSQL's own dependency graph answers (another schema's
    /// view, a rule, a function body: whatever it knows about). Null when all of them can go;
    /// otherwise the first one something else still depends on.
    /// </summary>
    private static async Task<string?> FirstUndroppableAsync(
        NpgsqlConnection liveConnection, string schema, IReadOnlyList<string> viewsInDropOrder,
        CancellationToken cancellationToken)
    {
        await using var transaction = await liveConnection.BeginTransactionAsync(cancellationToken);
        try
        {
            foreach (var name in viewsInDropOrder)
            {
                await using var drop = new NpgsqlCommand(
                    $"DROP VIEW {DesiredStateRenderer.Quote(schema)}.{DesiredStateRenderer.Quote(name)} RESTRICT",
                    liveConnection, transaction);
                try
                {
                    await drop.ExecuteNonQueryAsync(cancellationToken);
                }
                catch (PostgresException ex) when (ex.SqlState == DependentObjectsStillExist)
                {
                    return name;
                }
            }
            return null;
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
    /// Rewrites the file's CREATE VIEW into the probe that runs inside the
    /// shadow schema: its own target becomes a throwaway name in the shadow,
    /// and every <em>table</em> reference qualified to the source schema in
    /// its query body is retargeted there too, through
    /// <see cref="Shadow.SchemaRewriter.RetargetQueryReferences"/> — a tree
    /// rewrite (never text), like the one the desired tables themselves went
    /// through to land in the shadow. The probe connection's search_path
    /// covers bare references (which may also be CTE names, so they stay
    /// bare), but a body written as <c>FROM public.t</c> names its schema
    /// explicitly and would otherwise resolve past the shadow to the live
    /// table — which at <c>diff</c> time does not yet carry the column the
    /// desired view depends on. Both forms are ordinary desired-state SQL; a
    /// schema-qualified body is the usual shape of generated files. Function
    /// and type qualifiers, and references to some other schema, pass through
    /// untouched: routines and types are not in the shadow, and cross-schema
    /// scope is a later slice (ADR-0007).
    /// </summary>
    /// <summary>
    /// A function or procedure's idempotent script, re-aimed at the shadow: its own name lands
    /// in the shadow however the file qualified it, and references to the source schema inside
    /// it (defaults, a SQL-standard body) follow.
    /// </summary>
    internal static string RetargetRoutineForShadow(string createRoutineSql, string sourceSchema, string shadowSchema)
    {
        var parsed = Parser.Parse(createRoutineSql);
        if (parsed.Error is not null || parsed.Value is null
            || parsed.Value.Stmts.Count != 1 || parsed.Value.Stmts[0].Stmt.CreateFunctionStmt is not { } routine)
        {
            throw new InvalidOperationException(
                "Failed to re-parse a routine's own idempotent redefinition script for probing — " +
                "this is an internal inconsistency, please report it.");
        }

        var name = routine.Funcname;
        if (name.Count == 1)
        {
            name.Insert(0, new Node { String = new PgSqlParser.String { Sval = shadowSchema } });
        }
        else if (name.Count == 2 && name[0].String is { } qualifier)
        {
            qualifier.Sval = shadowSchema;
        }
        SchemaRewriter.RetargetQueryReferences(parsed.Value, sourceSchema, shadowSchema);

        var deparsed = Parser.Deparse(parsed.Value);
        if (deparsed.Error is not null || deparsed.Value is null)
        {
            throw new InvalidOperationException(
                $"Failed to render a probe redefinition of a routine ({deparsed.Error?.Message}) — " +
                "this is an internal inconsistency, please report it.");
        }
        return deparsed.Value;
    }

    internal static string RetargetForProbe(
        string createViewSql, string probeName, string sourceSchema, string shadowSchema)
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
        view.View.Schemaname = shadowSchema;   // the probe lands in the shadow, however the file qualified it
        view.Replace = false;                  // the probe name never pre-exists
        SchemaRewriter.RetargetQueryReferences(parsed.Value, sourceSchema, shadowSchema);

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
