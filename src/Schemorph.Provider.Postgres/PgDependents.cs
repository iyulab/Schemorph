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
/// Two kinds of dependent block a drop in practice: a view (or materialized view) that reads the
/// relation or one of its columns, and a foreign key on another table that references it. The
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
        ORDER BY 1
        """;

    /// <summary>
    /// Each dependent as one phrase — <c>view "s"."v" (on "t"."c")</c>, <c>foreign key "fk" on "s"."t"
    /// (references "t")</c> — or an empty list when there are none or the lookup failed.
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
