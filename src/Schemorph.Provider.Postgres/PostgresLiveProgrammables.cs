using Npgsql;
using Schemorph.Core.Providers;

namespace Schemorph.Provider.Postgres;

/// <summary>
/// Which of the desired programmable objects the live catalog actually holds —
/// by name and kind, in the target schema, nothing about their definitions.
/// Names follow <see cref="PostgresProgrammables"/>'s bare-name convention: a view
/// or routine is its own name, a trigger is <c>table.trigger</c>. A routine is
/// present when an overload with the file's argument types is — a file declares one
/// signature, not every overload of its name (<see cref="DeclaredSignatures"/>).
/// </summary>
internal static class PostgresLiveProgrammables
{
    private const string ViewsSql = """
        SELECT c.relname
        FROM pg_class c
        JOIN pg_namespace n ON n.oid = c.relnamespace
        WHERE n.nspname = @schema AND c.relkind IN ('v', 'm')
        """;

    private const string TriggersSql = """
        SELECT c.relname || '.' || t.tgname
        FROM pg_trigger t
        JOIN pg_class c ON c.oid = t.tgrelid
        JOIN pg_namespace n ON n.oid = c.relnamespace
        WHERE n.nspname = @schema AND NOT t.tgisinternal
        """;

    public static async Task<IReadOnlyList<ProgrammableObjectInfo>> FilterExistingAsync(
        string connectionString, string schema, IReadOnlyList<ProgrammableObjectInfo> objects,
        CancellationToken cancellationToken, IApplySession? session = null)
    {
        if (objects.Count == 0) return Array.Empty<ProgrammableObjectInfo>();

        await using var lease = await PostgresReadConnection.OpenAsync(connectionString, session, cancellationToken);
        var connection = lease.Connection;

        var views = await ReadNamesAsync(connection, ViewsSql, schema, cancellationToken);
        var routines = await ReadRoutinesAsync(connection, schema, cancellationToken);
        var triggers = await ReadNamesAsync(connection, TriggersSql, schema, cancellationToken);
        var declaredRoutines = PostgresProgrammables.DeclaredRoutinesOf(objects)
            .ToDictionary(r => r.ObjectName, StringComparer.Ordinal);
        var signatures = await DeclaredSignatures.ResolveAsync(lease, schema, declaredRoutines.Values.ToList(), cancellationToken);

        return objects
            .Where(o => o.ObjectType switch
            {
                "View" => views.Contains(o.ObjectName),
                "ScalarFunction" or "TableValuedFunction" or "Procedure" =>
                    routines.Any(r => r.Name == declaredRoutines[o.ObjectName].Name
                                      && signatures.DeclaresAs(o.ObjectName, r.ArgumentTypes)),
                "DmlTrigger" => triggers.Contains(o.ObjectName),
                _ => false,   // a kind this provider does not manage cannot be vouched for
            })
            .ToList();
    }

    /// <summary>
    /// A live view reading a relation in the target schema: the whole relation
    /// (<paramref name="Column"/> null) or one of its columns — the granularity PostgreSQL
    /// itself records, and so exactly what it refuses to drop or retype while the view exists.
    /// </summary>
    public sealed record ViewRead(string View, string Source, string? Column);

    // A view's rewrite rule depends on each relation it selects from and on each column it
    // reads (refobjsubid = the column's attnum); both ends in the target schema.
    private const string ViewReadsSql = """
        SELECT DISTINCT dependent.relname, source.relname, a.attname
        FROM pg_depend d
        JOIN pg_rewrite r ON r.oid = d.objid
        JOIN pg_class dependent ON dependent.oid = r.ev_class
        JOIN pg_class source ON source.oid = d.refobjid
        JOIN pg_namespace dn ON dn.oid = dependent.relnamespace
        JOIN pg_namespace sn ON sn.oid = source.relnamespace
        LEFT JOIN pg_attribute a ON a.attrelid = source.oid AND a.attnum = d.refobjsubid AND d.refobjsubid > 0
        WHERE d.classid = 'pg_rewrite'::regclass AND d.refclassid = 'pg_class'::regclass
          AND dependent.relkind = 'v' AND dependent.oid <> source.oid
          AND dn.nspname = @schema AND sn.nspname = @schema
        ORDER BY 1, 2, 3
        """;

    /// <summary>Every relation and column each live view in the target schema reads.</summary>
    public static async Task<IReadOnlyList<ViewRead>> ReadViewReadsAsync(
        string connectionString, string schema, CancellationToken cancellationToken, IApplySession? session = null)
    {
        await using var lease = await PostgresReadConnection.OpenAsync(connectionString, session, cancellationToken);
        var connection = lease.Connection;
        await using var command = new NpgsqlCommand(ViewReadsSql, connection);
        command.Parameters.AddWithValue("schema", schema);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var reads = new List<ViewRead>();
        while (await reader.ReadAsync(cancellationToken))
        {
            reads.Add(new ViewRead(reader.GetString(0), reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2)));
        }
        return reads;
    }

    /// <summary>A live view in the target schema that calls a routine in the target schema.</summary>
    /// <param name="Signature">The overload it calls, in <see cref="LiveRoutine.Signature"/>'s form.</param>
    public sealed record ViewCall(string View, string Routine, string Signature);

    // A view's rewrite rule depends on each function it calls; both ends in the target schema.
    private const string ViewCallsSql = """
        SELECT DISTINCT dependent.relname, p.proname,
               p.proname || '(' || pg_get_function_identity_arguments(p.oid) || ')'
        FROM pg_depend d
        JOIN pg_rewrite r ON r.oid = d.objid
        JOIN pg_class dependent ON dependent.oid = r.ev_class
        JOIN pg_proc p ON p.oid = d.refobjid
        JOIN pg_namespace dn ON dn.oid = dependent.relnamespace
        JOIN pg_namespace pn ON pn.oid = p.pronamespace
        WHERE d.classid = 'pg_rewrite'::regclass AND d.refclassid = 'pg_proc'::regclass
          AND dependent.relkind = 'v'
          AND dn.nspname = @schema AND pn.nspname = @schema
        ORDER BY 1, 2
        """;

    /// <summary>Every routine in the target schema each live view there calls.</summary>
    public static async Task<IReadOnlyList<ViewCall>> ReadViewCallsAsync(
        string connectionString, string schema, CancellationToken cancellationToken, IApplySession? session = null)
    {
        await using var lease = await PostgresReadConnection.OpenAsync(connectionString, session, cancellationToken);
        await using var command = new NpgsqlCommand(ViewCallsSql, lease.Connection);
        command.Parameters.AddWithValue("schema", schema);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var calls = new List<ViewCall>();
        while (await reader.ReadAsync(cancellationToken))
        {
            calls.Add(new ViewCall(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        }
        return calls;
    }

    /// <summary>A live programmable object the desired state does not declare, and how to drop it.</summary>
    /// <param name="ObjectName">The bare name — or, for an overload of a routine a file declares
    /// with other argument types, that overload's <see cref="LiveRoutine.Signature"/>.</param>
    /// <param name="DropStatements">One per overload for a routine; one otherwise.</param>
    /// <param name="Signatures">The routine overloads it drops; empty for any other kind.</param>
    public sealed record Undeclared(
        string ObjectName, string ObjectType, IReadOnlyList<string> DropStatements, IReadOnlyList<string> Signatures)
    {
        public Undeclared(string objectName, string objectType, IReadOnlyList<string> dropStatements)
            : this(objectName, objectType, dropStatements, []) { }
    }

    private const string UndeclaredTriggersSql = """
        SELECT c.relname || '.' || t.tgname,
               format('DROP TRIGGER %I ON %I.%I;', t.tgname, n.nspname, c.relname)
        FROM pg_trigger t
        JOIN pg_class c ON c.oid = t.tgrelid
        JOIN pg_namespace n ON n.oid = c.relnamespace
        WHERE n.nspname = @schema AND NOT t.tgisinternal
        ORDER BY 1
        """;

    // Plain views only. A materialized view holds rows, and one no file declares is
    // not this provider's to drop: it under-claims rather than guess.
    private const string UndeclaredViewsSql = """
        SELECT c.relname, format('DROP VIEW %I.%I;', n.nspname, c.relname)
        FROM pg_class c
        JOIN pg_namespace n ON n.oid = c.relnamespace
        WHERE n.nspname = @schema AND c.relkind = 'v'
          AND NOT EXISTS (SELECT 1 FROM pg_depend d
                          WHERE d.classid = 'pg_class'::regclass AND d.objid = c.oid AND d.deptype = 'e')
        ORDER BY 1
        """;

    // Which view reads which: a view's rewrite rule depends on every relation it selects from.
    private const string ViewDependenciesSql = """
        SELECT DISTINCT dependent.relname, source.relname
        FROM pg_depend d
        JOIN pg_rewrite r ON r.oid = d.objid
        JOIN pg_class dependent ON dependent.oid = r.ev_class
        JOIN pg_class source ON source.oid = d.refobjid
        JOIN pg_namespace n ON n.oid = source.relnamespace
        WHERE d.classid = 'pg_rewrite'::regclass AND d.refclassid = 'pg_class'::regclass
          AND source.relkind = 'v' AND dependent.oid <> source.oid AND n.nspname = @schema
        """;

    /// <summary>A live function or procedure in the target schema.</summary>
    /// <param name="Signature">Its name and identity arguments — <c>f(x integer)</c> — the form a plan names one overload by.</param>
    /// <param name="ArgumentTypes">The types its identity is made of — the call arguments (<c>proargtypes</c>), and a procedure's OUT arguments too, as <c>pg_get_function_identity_arguments</c> counts them.</param>
    /// <param name="ExtensionMember">Whether an extension owns it — the extension's, never the files'.</param>
    internal sealed record LiveRoutine(
        string Name, string Type, string Drop, string Signature, IReadOnlyList<uint> ArgumentTypes, bool ExtensionMember);

    // Each overload is dropped by its own signature.
    private const string LiveRoutinesSql = """
        SELECT p.proname,
               CASE WHEN p.prokind = 'p' THEN 'Procedure'
                    WHEN p.proretset THEN 'TableValuedFunction'
                    ELSE 'ScalarFunction' END,
               format('DROP %s %s;', CASE WHEN p.prokind = 'p' THEN 'PROCEDURE' ELSE 'FUNCTION' END,
                      p.oid::regprocedure),
               p.proname || '(' || pg_get_function_identity_arguments(p.oid) || ')',
               CASE WHEN p.proallargtypes IS NULL THEN p.proargtypes::oid[]
                    ELSE ARRAY(SELECT u.t FROM unnest(p.proallargtypes, p.proargmodes) WITH ORDINALITY AS u(t, m, i)
                               WHERE u.m IN ('i', 'b', 'v') OR (u.m = 'o' AND p.prokind = 'p')
                               ORDER BY u.i) END,
               EXISTS (SELECT 1 FROM pg_depend d
                       WHERE d.classid = 'pg_proc'::regclass AND d.objid = p.oid AND d.deptype = 'e')
        FROM pg_proc p
        JOIN pg_namespace n ON n.oid = p.pronamespace
        WHERE n.nspname = @schema AND p.prokind IN ('f', 'p')
        ORDER BY 1, 3
        """;

    private static async Task<List<LiveRoutine>> ReadRoutinesAsync(
        NpgsqlConnection connection, string schema, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(LiveRoutinesSql, connection);
        command.Parameters.AddWithValue("schema", schema);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var routines = new List<LiveRoutine>();
        while (await reader.ReadAsync(cancellationToken))
        {
            routines.Add(new LiveRoutine(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetFieldValue<uint[]>(4), reader.GetBoolean(5)));
        }
        return routines;
    }

    /// <summary>
    /// The argument types each declared routine's file names, resolved by the engine the way
    /// re-definition will resolve them — <c>to_regtype</c> on the target schema's search path —
    /// so a type spelled another way is still the same type.
    /// </summary>
    internal sealed class DeclaredSignatures
    {
        private readonly IReadOnlyList<(string ObjectName, string Name, uint[]? Types)> _declared;

        private DeclaredSignatures(IReadOnlyList<(string ObjectName, string Name, uint[]? Types)> declared) => _declared = declared;

        /// <summary>
        /// Whether the files declare the live overload <paramref name="name"/>(<paramref name="argumentTypes"/>) -
        /// whether any file declaring that name declares those types. A name one of whose files has types that
        /// could not be settled before apply (<c>%TYPE</c>, or a type that does not exist yet) declares every
        /// overload of it: what cannot be told apart is not dropped.
        /// </summary>
        public bool Declares(string name, IReadOnlyList<uint> argumentTypes)
            => _declared.Any(d => d.Name == name && (d.Types is null || d.Types.SequenceEqual(argumentTypes)));

        /// <summary>Whether the one declared routine <paramref name="objectName"/> is the live overload with <paramref name="argumentTypes"/>.</summary>
        public bool DeclaresAs(string objectName, IReadOnlyList<uint> argumentTypes)
            => _declared.Any(d => d.ObjectName == objectName && (d.Types is null || d.Types.SequenceEqual(argumentTypes)));

        /// <summary>
        /// Files that declare one overload twice, in spellings the parser keeps apart but the engine does not
        /// (a type with and without its schema): each group is the object names of one overload.
        /// </summary>
        public IReadOnlyList<IReadOnlyList<string>> SameOverload()
            => _declared.Where(d => d.Types is not null)
                .GroupBy(d => (d.Name, Key: string.Join(",", d.Types!)))
                .Where(g => g.Count() > 1)
                .Select(g => (IReadOnlyList<string>)g.Select(d => d.ObjectName).OrderBy(n => n, StringComparer.Ordinal).ToList())
                .ToList();

        public static async Task<DeclaredSignatures> ResolveAsync(
            PostgresReadConnection lease, string schema,
            IReadOnlyList<PostgresProgrammables.DeclaredRoutine> routines, CancellationToken cancellationToken)
        {
            var names = routines.Where(r => r.Types is not null).SelectMany(r => r.Types!)
                .Distinct(StringComparer.Ordinal).ToArray();
            var resolved = new Dictionary<string, uint?>(StringComparer.Ordinal);
            if (names.Length > 0)
            {
                // The search path is changed only for the probe, which is undone.
                await lease.ProbeAsync(async transaction =>
                {
                    await using (var path = new NpgsqlCommand(
                        "SELECT set_config('search_path', @path, true)", lease.Connection, transaction))
                    {
                        path.Parameters.AddWithValue("path", DesiredStateRenderer.Quote(schema));
                        await path.ExecuteNonQueryAsync(cancellationToken);
                    }
                    await using var command = new NpgsqlCommand(
                        "SELECT u.t, to_regtype(u.t)::oid FROM unnest(@names) AS u(t)", lease.Connection, transaction);
                    command.Parameters.AddWithValue("names", names);
                    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                    while (await reader.ReadAsync(cancellationToken))
                    {
                        resolved[reader.GetString(0)] = reader.IsDBNull(1) ? null : reader.GetFieldValue<uint>(1);
                    }
                    return true;
                }, cancellationToken);
            }

            return new DeclaredSignatures(routines.Select(r => (r.ObjectName, r.Name,
                r.Types is { } types && types.All(t => resolved.GetValueOrDefault(t) is not null)
                    ? types.Select(t => resolved[t]!.Value).ToArray()
                    : null)).ToList());
        }
    }

    /// <summary>
    /// The views, routines and triggers in the target schema that no desired-state file
    /// declares — the objects a deleted file leaves behind. Compared by name, the
    /// granularity the redefine strategy already works at — except a routine, which a file
    /// declares by name and argument types: an overload of a declared name with other
    /// argument types is undeclared on its own, under its signature (a file whose arguments
    /// changed leaves exactly that behind).
    /// </summary>
    /// <remarks>
    /// Returned in the order they can be dropped: triggers first (they depend on their
    /// function), then views with every view that reads another ahead of the one it
    /// reads, then routines (a view may call one). Dropping them in this order never
    /// trips over an object the same change set is about to remove.
    /// </remarks>
    public static async Task<UndeclaredRead> ReadUndeclaredAsync(
        string connectionString, string schema, IReadOnlySet<string> declaredNames,
        IReadOnlyList<PostgresProgrammables.DeclaredRoutine> declaredRoutines,
        CancellationToken cancellationToken, IApplySession? session = null)
    {
        await using var lease = await PostgresReadConnection.OpenAsync(connectionString, session, cancellationToken);
        var connection = lease.Connection;

        var result = new List<Undeclared>();

        foreach (var (name, drop) in await ReadPairsAsync(connection, UndeclaredTriggersSql, schema, cancellationToken))
        {
            if (!declaredNames.Contains(name)) result.Add(new Undeclared(name, "DmlTrigger", [drop]));
        }

        var views = (await ReadPairsAsync(connection, UndeclaredViewsSql, schema, cancellationToken))
            .Where(v => !declaredNames.Contains(v.First))
            .ToDictionary(v => v.First, v => v.Second, StringComparer.Ordinal);
        var readers = (await ReadPairsAsync(connection, ViewDependenciesSql, schema, cancellationToken))
            .Where(d => views.ContainsKey(d.First) && views.ContainsKey(d.Second))
            .ToList();
        foreach (var name in ReadersFirst(views.Keys, readers))
        {
            result.Add(new Undeclared(name, "View", [views[name]]));
        }

        var routines = (await ReadRoutinesAsync(connection, schema, cancellationToken))
            .Where(r => !r.ExtensionMember).ToList();
        var routineNames = declaredRoutines.Select(r => r.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var group in routines.Where(r => !routineNames.Contains(r.Name)).GroupBy(r => r.Name))
        {
            result.Add(new Undeclared(group.Key, group.First().Type,
                group.Select(r => r.Drop).ToList(), group.Select(r => r.Signature).ToList()));
        }
        var signatures = await DeclaredSignatures.ResolveAsync(lease, schema, declaredRoutines, cancellationToken);
        var leftBehind = routines.Where(r => routineNames.Contains(r.Name)
                                             && !signatures.Declares(r.Name, r.ArgumentTypes)).ToList();
        var kept = new List<KeptOverload>();
        if (leftBehind.Count > 0)
        {
            var holders = (await ReadPairsAsync(connection, OverloadHoldersSql, schema, cancellationToken))
                .ToLookup(h => h.First, h => h.Second, StringComparer.Ordinal);
            foreach (var routine in leftBehind)
            {
                if (holders[routine.Signature].ToList() is { Count: > 0 } held)
                {
                    kept.Add(new KeptOverload(routine.Signature, held));
                }
                else
                {
                    result.Add(new Undeclared(routine.Signature, routine.Type, [routine.Drop], [routine.Signature]));
                }
            }
        }

        return new UndeclaredRead(result, kept, signatures.SameOverload());
    }

    /// <summary>
    /// What <see cref="ReadUndeclaredAsync"/> found: the objects to drop, the overloads it would not, and the
    /// declared routines that turned out to be one overload written twice.
    /// </summary>
    public sealed record UndeclaredRead(
        IReadOnlyList<Undeclared> Objects, IReadOnlyList<KeptOverload> Kept, IReadOnlyList<IReadOnlyList<string>> SameOverload);

    /// <summary>
    /// An overload of a declared routine name that the files no longer declare, kept because something other
    /// than a view in the target schema still uses it.
    /// </summary>
    /// <param name="Dependents">The engine's description of each (<c>pg_describe_object</c>).</param>
    public sealed record KeptOverload(string Signature, IReadOnlyList<string> Dependents);

    // What holds on to a routine overload besides a view in the target schema — a CHECK constraint, a column
    // default or generation expression, an index expression, a materialized view, a trigger, a routine whose
    // SQL-standard body calls it, a view in another schema. Each refuses the overload's drop (2BP01), and the
    // drop runs ahead of the table statements that might have changed it, so it is not attempted. A view in
    // the target schema is not one: a declared view calling it is dropped first and re-created, an undeclared
    // one is dropped anyway.
    private const string OverloadHoldersSql = """
        SELECT DISTINCT p.proname || '(' || pg_get_function_identity_arguments(p.oid) || ')',
               pg_describe_object(d.classid, d.objid, d.objsubid)
        FROM pg_depend d
        JOIN pg_proc p ON d.refclassid = 'pg_proc'::regclass AND p.oid = d.refobjid
        JOIN pg_namespace n ON n.oid = p.pronamespace
        WHERE n.nspname = @schema AND d.deptype = 'n'
          AND NOT EXISTS (SELECT 1 FROM pg_rewrite r JOIN pg_class c ON c.oid = r.ev_class
                          WHERE d.classid = 'pg_rewrite'::regclass AND r.oid = d.objid
                            AND c.relkind = 'v' AND c.relnamespace = n.oid)
        ORDER BY 1, 2
        """;

    /// <summary>
    /// Orders views so a view that reads another comes before it (Kahn's algorithm over
    /// the reader → source edges), ties broken by name for a stable script.
    /// </summary>
    internal static IEnumerable<string> ReadersFirst(
        IEnumerable<string> views, IReadOnlyList<(string First, string Second)> readerToSource)
    {
        var remaining = views.ToHashSet(StringComparer.Ordinal);
        while (remaining.Count > 0)
        {
            // A view can go once no remaining view still reads it.
            var ready = remaining
                .Where(v => !readerToSource.Any(e => e.Second == v && remaining.Contains(e.First)))
                .OrderBy(v => v, StringComparer.Ordinal)
                .ToList();
            if (ready.Count == 0)
            {
                // A cycle among views cannot be created in PostgreSQL; drop the rest by name.
                ready = remaining.OrderBy(v => v, StringComparer.Ordinal).ToList();
            }
            foreach (var v in ready)
            {
                remaining.Remove(v);
                yield return v;
            }
        }
    }

    private static async Task<List<(string First, string Second)>> ReadPairsAsync(
        NpgsqlConnection connection, string sql, string schema, CancellationToken cancellationToken)
    {
        var pairs = new List<(string, string)>();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("schema", schema);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            pairs.Add((reader.GetString(0), reader.GetString(1)));
        }
        return pairs;
    }

    private static async Task<HashSet<string>> ReadNamesAsync(
        NpgsqlConnection connection, string sql, string schema, CancellationToken cancellationToken)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("schema", schema);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            names.Add(reader.GetString(0));
        }
        return names;
    }
}
