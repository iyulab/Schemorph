using Npgsql;

namespace Schemorph.Provider.Postgres;

/// <summary>
/// Reads a schema out of pg_catalog. The only place in this provider that knows
/// about a connection — everything downstream works on <see cref="PgTable"/>.
///
/// Every rendering (types, constraint definitions, index statements) comes from
/// the engine's own functions rather than being re-spelled here. That is the
/// technique ADR-0007 chose: the engine is the normalizer, so the comparison
/// layer holds no parser and no dialect table.
/// </summary>
internal static class CatalogReader
{
    public static async Task<IReadOnlyList<PgTable>> ReadTablesAsync(
        string connectionString, string schema, CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        var columns = await ReadColumnsAsync(connection, schema, cancellationToken);
        var constraints = await ReadConstraintsAsync(connection, schema, cancellationToken);
        var indexes = await ReadIndexesAsync(connection, schema, cancellationToken);

        var tableNames = columns.Keys
            .Union(constraints.Keys, StringComparer.Ordinal)
            .Union(indexes.Keys, StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.Ordinal);

        return tableNames
            .Select(name => new PgTable(
                schema,
                name,
                columns.TryGetValue(name, out var c) ? c : [],
                constraints.TryGetValue(name, out var k) ? k : [],
                indexes.TryGetValue(name, out var i) ? i : []))
            .ToList();
    }

    // attidentity/attgenerated route the pg_attrdef expression: for a generated
    // column it is the generation expression, not a DEFAULT — rendering it as
    // DEFAULT is wrong SQL (cycle-76). Identity sequence options come from the
    // internally-dependent ('i') sequence so non-default START/INCREMENT/…
    // survive a round trip; seqtypid lets the renderer tell a real MAXVALUE
    // from the type's own ceiling.
    private const string ColumnsSql = """
        SELECT c.relname, a.attname,
               format_type(a.atttypid, a.atttypmod),
               a.attnotnull,
               pg_get_expr(d.adbin, d.adrelid),
               a.attidentity, a.attgenerated,
               s.seqstart, s.seqincrement, s.seqmin, s.seqmax, s.seqcache, s.seqcycle,
               format_type(s.seqtypid, NULL)
        FROM pg_attribute a
        JOIN pg_class c ON c.oid = a.attrelid
        JOIN pg_namespace n ON n.oid = c.relnamespace
        LEFT JOIN pg_attrdef d ON d.adrelid = a.attrelid AND d.adnum = a.attnum
        LEFT JOIN pg_depend dep ON a.attidentity <> ''
             AND dep.classid = 'pg_class'::regclass AND dep.refclassid = 'pg_class'::regclass
             AND dep.refobjid = a.attrelid AND dep.refobjsubid = a.attnum AND dep.deptype = 'i'
        LEFT JOIN pg_sequence s ON s.seqrelid = dep.objid
        WHERE n.nspname = @schema AND c.relkind = 'r'
          AND a.attnum > 0 AND NOT a.attisdropped
        ORDER BY c.relname, a.attnum
        """;

    // conindid <> 0 identifies the index a constraint owns; emitting that index
    // separately would produce a file that cannot be applied twice — the
    // constraint creates it, then CREATE INDEX collides.
    private const string IndexesSql = """
        SELECT c.relname, i.relname, pg_get_indexdef(i.oid)
        FROM pg_index x
        JOIN pg_class c ON c.oid = x.indrelid
        JOIN pg_class i ON i.oid = x.indexrelid
        JOIN pg_namespace n ON n.oid = c.relnamespace
        WHERE n.nspname = @schema AND c.relkind = 'r'
          AND NOT EXISTS (SELECT 1 FROM pg_constraint con WHERE con.conindid = i.oid)
        ORDER BY c.relname, i.relname
        """;

    private const string ConstraintsSql = """
        SELECT c.relname, con.conname, pg_get_constraintdef(con.oid)
        FROM pg_constraint con
        JOIN pg_class c ON c.oid = con.conrelid
        JOIN pg_namespace n ON n.oid = c.relnamespace
        WHERE n.nspname = @schema AND c.relkind = 'r'
        ORDER BY c.relname, con.conname
        """;

    private static async Task<Dictionary<string, List<PgColumn>>> ReadColumnsAsync(
        NpgsqlConnection connection, string schema, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, List<PgColumn>>(StringComparer.Ordinal);
        await foreach (var row in QueryAsync(connection, ColumnsSql, schema, cancellationToken))
        {
            var expression = row.IsDBNull(4) ? null : row.GetString(4);
            var identity = row.GetChar(5) switch
            {
                'a' => PgIdentity.Always,
                'd' => PgIdentity.ByDefault,
                _ => PgIdentity.None,
            };
            var generated = row.GetChar(6) == 's';

            Bucket(result, row.GetString(0)).Add(new PgColumn(
                row.GetString(1),
                row.GetString(2),
                row.GetBoolean(3),
                Default: generated ? null : expression,
                identity,
                GeneratedAs: generated ? expression : null,
                IdentityOptions: identity == PgIdentity.None || row.IsDBNull(7)
                    ? null
                    : IdentityOptions(
                        start: row.GetInt64(7), increment: row.GetInt64(8),
                        min: row.GetInt64(9), max: row.GetInt64(10),
                        cache: row.GetInt64(11), cycle: row.GetBoolean(12),
                        sequenceType: row.GetString(13))));
        }
        return result;
    }

    /// <summary>
    /// Renders only the options that differ from what the engine would choose on
    /// its own, so the common identity column stays a clean one-liner. The
    /// defaults depend on the sequence's type and direction: ascending starts at
    /// MINVALUE (1), and MAXVALUE defaults to the type's own ceiling.
    /// </summary>
    internal static string? IdentityOptions(
        long start, long increment, long min, long max, long cache, bool cycle, string sequenceType)
    {
        var typeMax = sequenceType switch
        {
            "smallint" => (long)short.MaxValue,
            "integer" => int.MaxValue,
            _ => long.MaxValue,
        };
        var typeMin = sequenceType switch
        {
            "smallint" => (long)short.MinValue,
            "integer" => int.MinValue,
            _ => long.MinValue,
        };
        var (defaultMin, defaultMax) = increment > 0 ? (1L, typeMax) : (typeMin, -1L);
        var defaultStart = increment > 0 ? defaultMin : defaultMax;

        var options = new List<string>();
        if (increment != 1) options.Add($"INCREMENT BY {increment}");
        if (min != defaultMin) options.Add($"MINVALUE {min}");
        if (max != defaultMax) options.Add($"MAXVALUE {max}");
        if (start != defaultStart) options.Add($"START WITH {start}");
        if (cache != 1) options.Add($"CACHE {cache}");
        if (cycle) options.Add("CYCLE");

        return options.Count == 0 ? null : string.Join(" ", options);
    }

    private static async Task<Dictionary<string, List<PgConstraint>>> ReadConstraintsAsync(
        NpgsqlConnection connection, string schema, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, List<PgConstraint>>(StringComparer.Ordinal);
        await foreach (var row in QueryAsync(connection, ConstraintsSql, schema, cancellationToken))
        {
            Bucket(result, row.GetString(0)).Add(new PgConstraint(row.GetString(1), row.GetString(2)));
        }
        return result;
    }

    private static async Task<Dictionary<string, List<PgIndex>>> ReadIndexesAsync(
        NpgsqlConnection connection, string schema, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, List<PgIndex>>(StringComparer.Ordinal);
        await foreach (var row in QueryAsync(connection, IndexesSql, schema, cancellationToken))
        {
            Bucket(result, row.GetString(0)).Add(new PgIndex(row.GetString(1), row.GetString(2)));
        }
        return result;
    }

    private static List<T> Bucket<T>(Dictionary<string, List<T>> map, string key)
        => map.TryGetValue(key, out var list) ? list : map[key] = [];

    private static async IAsyncEnumerable<NpgsqlDataReader> QueryAsync(
        NpgsqlConnection connection, string sql, string schema,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("schema", schema);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            yield return reader;
        }
    }
}
