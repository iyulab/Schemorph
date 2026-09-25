using Npgsql;
using Schemorph.Core.Providers;

namespace Schemorph.Provider.Postgres;

/// <summary>
/// Which of the desired programmable objects the live catalog actually holds —
/// by name and kind, in the target schema, nothing about their definitions.
/// Names follow <see cref="PgProgrammables"/>'s bare-name convention: a view
/// or routine is its own name, a trigger is <c>table.trigger</c>. Routines are
/// matched by name alone (an overload set counts as present — the
/// redefinition script is <c>CREATE OR REPLACE</c> either way).
/// </summary>
internal static class PgLiveProgrammables
{
    private const string ViewsSql = """
        SELECT c.relname
        FROM pg_class c
        JOIN pg_namespace n ON n.oid = c.relnamespace
        WHERE n.nspname = @schema AND c.relkind IN ('v', 'm')
        """;

    private const string RoutinesSql = """
        SELECT DISTINCT p.proname
        FROM pg_proc p
        JOIN pg_namespace n ON n.oid = p.pronamespace
        WHERE n.nspname = @schema
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
        CancellationToken cancellationToken)
    {
        if (objects.Count == 0) return Array.Empty<ProgrammableObjectInfo>();

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        var views = await ReadNamesAsync(connection, ViewsSql, schema, cancellationToken);
        var routines = await ReadNamesAsync(connection, RoutinesSql, schema, cancellationToken);
        var triggers = await ReadNamesAsync(connection, TriggersSql, schema, cancellationToken);

        return objects
            .Where(o => o.ObjectType switch
            {
                "View" => views.Contains(o.ObjectName),
                "ScalarFunction" or "TableValuedFunction" or "Procedure" => routines.Contains(o.ObjectName),
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
        string connectionString, string schema, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
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

    /// <summary>A live programmable object the desired state does not declare, and how to drop it.</summary>
    /// <param name="DropStatements">One per overload for a routine; one otherwise.</param>
    public sealed record Undeclared(string ObjectName, string ObjectType, IReadOnlyList<string> DropStatements);

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

    // Routines by name, as everywhere else here; each overload is dropped by its own
    // signature. Members of an extension belong to the extension, never to the files.
    private const string UndeclaredRoutinesSql = """
        SELECT p.proname,
               CASE WHEN p.prokind = 'p' THEN 'Procedure'
                    WHEN p.proretset THEN 'TableValuedFunction'
                    ELSE 'ScalarFunction' END,
               format('DROP %s %s;', CASE WHEN p.prokind = 'p' THEN 'PROCEDURE' ELSE 'FUNCTION' END,
                      p.oid::regprocedure)
        FROM pg_proc p
        JOIN pg_namespace n ON n.oid = p.pronamespace
        WHERE n.nspname = @schema AND p.prokind IN ('f', 'p')
          AND NOT EXISTS (SELECT 1 FROM pg_depend d
                          WHERE d.classid = 'pg_proc'::regclass AND d.objid = p.oid AND d.deptype = 'e')
        ORDER BY 1, 3
        """;

    /// <summary>
    /// The views, routines and triggers in the target schema that no desired-state file
    /// declares — the objects a deleted file leaves behind. Compared by name, the
    /// granularity the redefine strategy already works at.
    /// </summary>
    /// <remarks>
    /// Returned in the order they can be dropped: triggers first (they depend on their
    /// function), then views with every view that reads another ahead of the one it
    /// reads, then routines (a view may call one). Dropping them in this order never
    /// trips over an object the same change set is about to remove.
    /// </remarks>
    public static async Task<IReadOnlyList<Undeclared>> ReadUndeclaredAsync(
        string connectionString, string schema, IReadOnlySet<string> declaredNames,
        CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

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

        await using (var command = new NpgsqlCommand(UndeclaredRoutinesSql, connection))
        {
            command.Parameters.AddWithValue("schema", schema);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var routines = new List<(string Name, string Type, string Drop)>();
            while (await reader.ReadAsync(cancellationToken))
            {
                routines.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
            }
            foreach (var group in routines.Where(r => !declaredNames.Contains(r.Name)).GroupBy(r => r.Name))
            {
                result.Add(new Undeclared(group.Key, group.First().Type, group.Select(r => r.Drop).ToList()));
            }
        }

        return result;
    }

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
