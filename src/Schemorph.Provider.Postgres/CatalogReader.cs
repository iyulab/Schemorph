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

    private const string ColumnsSql = """
        SELECT c.relname, a.attname,
               format_type(a.atttypid, a.atttypmod),
               a.attnotnull,
               pg_get_expr(d.adbin, d.adrelid)
        FROM pg_attribute a
        JOIN pg_class c ON c.oid = a.attrelid
        JOIN pg_namespace n ON n.oid = c.relnamespace
        LEFT JOIN pg_attrdef d ON d.adrelid = a.attrelid AND d.adnum = a.attnum
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
        WHERE n.nspname = @schema
        ORDER BY c.relname, con.conname
        """;

    private static async Task<Dictionary<string, List<PgColumn>>> ReadColumnsAsync(
        NpgsqlConnection connection, string schema, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, List<PgColumn>>(StringComparer.Ordinal);
        await foreach (var row in QueryAsync(connection, ColumnsSql, schema, cancellationToken))
        {
            Bucket(result, row.GetString(0)).Add(new PgColumn(
                row.GetString(1),
                row.GetString(2),
                row.GetBoolean(3),
                row.IsDBNull(4) ? null : row.GetString(4)));
        }
        return result;
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
