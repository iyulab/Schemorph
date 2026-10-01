using Npgsql;

namespace Schemorph.Provider.Postgres;

/// <summary>
/// What still depends on the relations an apply was changing — asked of the catalog after the
/// engine refused a drop with <c>2BP01</c>.
/// </summary>
/// <remarks>
/// <para>
/// The engine names the dependents only in the error's <c>Detail</c>. Npgsql withholds it unless the
/// connection sets <c>Include Error Detail</c>, and this tool does not: the same field carries row
/// values for other errors (a duplicate key's value), and a failure message travels to CI logs and
/// MCP clients. The catalog answers the same question without row data.
/// </para>
/// <para>
/// What blocks a drop: a view (or materialized view) that reads the relation or one of its columns;
/// a foreign key on another table that references it; a function whose SQL-standard body
/// (<c>BEGIN ATOMIC</c> or <c>RETURN</c>) reads it — a PL/pgSQL body is not tracked by the engine and blocks nothing;
/// and, for a column, a trigger that fires on updates of that column or a row-level security policy
/// whose expression reads it. Each is a normal dependency in <c>pg_depend</c>; the automatic ones —
/// a trigger or policy on the table itself — go with a dropped table and are not listed for it. The
/// answer is best-effort — a failed lookup leaves the engine's message and hint as they were.
/// </para>
/// </remarks>
internal static class PgDependents
{
    private const int MaxListed = 10;

    private const string Query = """
        SELECT DISTINCT
            CASE dc.relkind WHEN 'm' THEN 'materialized view' ELSE 'view' END
                || ' ' || ('"' || replace(dn.nspname, '"', '""') || '"') || '.' || ('"' || replace(dc.relname, '"', '""') || '"')
                || ' (on ' || ('"' || replace(sc.relname, '"', '""') || '"')
                || COALESCE('.' || ('"' || replace(a.attname, '"', '""') || '"'), '') || ')' AS dependent
        FROM pg_depend d
        JOIN pg_rewrite r ON d.classid = 'pg_rewrite'::regclass AND r.oid = d.objid
        JOIN pg_class dc ON dc.oid = r.ev_class
        JOIN pg_namespace dn ON dn.oid = dc.relnamespace
        JOIN pg_class sc ON d.refclassid = 'pg_class'::regclass AND sc.oid = d.refobjid
        JOIN pg_namespace sn ON sn.oid = sc.relnamespace
        LEFT JOIN pg_attribute a ON d.refobjsubid > 0 AND a.attrelid = sc.oid AND a.attnum = d.refobjsubid
        WHERE sn.nspname = @schema AND sc.relname = ANY(@names) AND dc.oid <> sc.oid
        UNION
        SELECT DISTINCT
            'foreign key ' || ('"' || replace(con.conname, '"', '""') || '"') || ' on ' || ('"' || replace(cn.nspname, '"', '""') || '"') || '.' || ('"' || replace(cc.relname, '"', '""') || '"')
                || ' (references ' || ('"' || replace(sc.relname, '"', '""') || '"') || ')'
        FROM pg_constraint con
        JOIN pg_class cc ON cc.oid = con.conrelid
        JOIN pg_namespace cn ON cn.oid = cc.relnamespace
        JOIN pg_class sc ON sc.oid = con.confrelid
        JOIN pg_namespace sn ON sn.oid = sc.relnamespace
        WHERE con.contype = 'f' AND con.conrelid <> con.confrelid
          AND sn.nspname = @schema AND sc.relname = ANY(@names)
        UNION
        SELECT DISTINCT
            'function ' || ('"' || replace(pn.nspname, '"', '""') || '"') || '.' || ('"' || replace(p.proname, '"', '""') || '"')
                || '(' || pg_get_function_identity_arguments(p.oid) || ')'
                || ' (on ' || ('"' || replace(sc.relname, '"', '""') || '"')
                || COALESCE('.' || ('"' || replace(a.attname, '"', '""') || '"'), '') || ')'
        FROM pg_depend d
        JOIN pg_proc p ON d.classid = 'pg_proc'::regclass AND p.oid = d.objid
        JOIN pg_namespace pn ON pn.oid = p.pronamespace
        JOIN pg_class sc ON d.refclassid = 'pg_class'::regclass AND sc.oid = d.refobjid
        JOIN pg_namespace sn ON sn.oid = sc.relnamespace
        LEFT JOIN pg_attribute a ON d.refobjsubid > 0 AND a.attrelid = sc.oid AND a.attnum = d.refobjsubid
        WHERE d.deptype = 'n' AND sn.nspname = @schema AND sc.relname = ANY(@names)
        UNION
        SELECT DISTINCT
            'trigger ' || ('"' || replace(t.tgname, '"', '""') || '"') || ' on ' || ('"' || replace(sc.relname, '"', '""') || '"')
                || ' (on ' || ('"' || replace(sc.relname, '"', '""') || '"') || '.' || ('"' || replace(a.attname, '"', '""') || '"') || ')'
        FROM pg_depend d
        JOIN pg_trigger t ON d.classid = 'pg_trigger'::regclass AND t.oid = d.objid
        JOIN pg_class sc ON d.refclassid = 'pg_class'::regclass AND sc.oid = d.refobjid
        JOIN pg_namespace sn ON sn.oid = sc.relnamespace
        JOIN pg_attribute a ON a.attrelid = sc.oid AND a.attnum = d.refobjsubid
        WHERE d.deptype = 'n' AND d.refobjsubid > 0 AND sn.nspname = @schema AND sc.relname = ANY(@names)
        UNION
        SELECT DISTINCT
            'policy ' || ('"' || replace(pol.polname, '"', '""') || '"') || ' on ' || ('"' || replace(sc.relname, '"', '""') || '"')
                || ' (on ' || ('"' || replace(sc.relname, '"', '""') || '"') || '.' || ('"' || replace(a.attname, '"', '""') || '"') || ')'
        FROM pg_depend d
        JOIN pg_policy pol ON d.classid = 'pg_policy'::regclass AND pol.oid = d.objid
        JOIN pg_class sc ON d.refclassid = 'pg_class'::regclass AND sc.oid = d.refobjid
        JOIN pg_namespace sn ON sn.oid = sc.relnamespace
        JOIN pg_attribute a ON a.attrelid = sc.oid AND a.attnum = d.refobjsubid
        WHERE d.deptype = 'n' AND d.refobjsubid > 0 AND sn.nspname = @schema AND sc.relname = ANY(@names)
        ORDER BY 1
        """;

    /// <summary>
    /// Each dependent as one phrase — <c>view "s"."v" (on "t"."c")</c>, <c>foreign key "fk" on "s"."t"
    /// (references "t")</c>, <c>function "s"."f"(args) (on "t"."c")</c>, <c>trigger "tg" on "t" (on "t"."c")</c>,
    /// <c>policy "p" on "t" (on "t"."c")</c> — or an empty list when there are none or the lookup failed.
    /// </summary>
    public static async Task<IReadOnlyList<string>> FindAsync(string connectionString, string schema,
        IReadOnlyCollection<string> relations, CancellationToken cancellationToken)
    {
        if (relations.Count == 0) return [];
        try
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = new NpgsqlCommand(Query, connection);
            command.Parameters.AddWithValue("schema", schema);
            command.Parameters.AddWithValue("names", relations.ToArray());
            var found = new List<string>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) found.Add(reader.GetString(0));
            return found;
        }
        catch (Exception e) when (e is NpgsqlException or InvalidOperationException)
        {
            return [];
        }
    }

    /// <summary>The hint with the dependents appended, at most <see cref="MaxListed"/> of them.</summary>
    public static string AppendTo(string? hint, IReadOnlyList<string> dependents)
    {
        var listed = string.Join("; ", dependents.Take(MaxListed))
                     + (dependents.Count > MaxListed ? $"; … (+{dependents.Count - MaxListed})" : "");
        return $"{hint} Still depending on what this apply changes: {listed}.".TrimStart();
    }
}
