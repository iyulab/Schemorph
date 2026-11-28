using Npgsql;

namespace Schemorph.Provider.Postgres;

/// <summary>A plain view as the engine reconstructs it.</summary>
/// <param name="Query">The view's query, <c>pg_get_viewdef</c>'s rendering.</param>
/// <param name="Options">Its <c>WITH (…)</c> options (<c>security_barrier</c>, <c>check_option</c>, …), or null.</param>
internal sealed record PostgresViewDefinition(string Schema, string Name, string Query, string? Options);

/// <summary>A function or procedure as the engine reconstructs it.</summary>
/// <param name="IdentityArguments">What tells its overloads apart — <c>pg_get_function_identity_arguments</c>.</param>
/// <param name="Definition">The whole <c>CREATE OR REPLACE</c> statement, <c>pg_get_functiondef</c>'s rendering.</param>
internal sealed record PostgresRoutineDefinition(
    string Schema, string Name, bool IsProcedure, string IdentityArguments, string Definition);

/// <summary>A trigger as the engine reconstructs it.</summary>
/// <param name="Definition">The <c>CREATE TRIGGER</c> statement, <c>pg_get_triggerdef</c>'s rendering.</param>
internal sealed record PostgresTriggerDefinition(string Schema, string Table, string Name, string Definition);

internal sealed record PostgresProgrammableDefinitions(
    IReadOnlyList<PostgresViewDefinition> Views,
    IReadOnlyList<PostgresRoutineDefinition> Routines,
    IReadOnlyList<PostgresTriggerDefinition> Triggers);

/// <summary>
/// Reads the target schema's programmable objects with the engine's own reconstruction
/// functions, for <c>inspect</c>. Exactly the objects the comparison treats as declarable —
/// the same rule <see cref="PostgresLiveProgrammables"/> uses to decide what an undeclared
/// one is: plain views (a materialized view holds rows and is never dropped as undeclared),
/// functions and procedures (not aggregates or window functions), and user triggers. An
/// extension's members are the extension's and are left out.
/// </summary>
internal static class LiveDefinitionReader
{
    private const string ViewsSql = """
        SELECT c.relname, pg_get_viewdef(c.oid, true), array_to_string(c.reloptions, ', ')
        FROM pg_class c
        JOIN pg_namespace n ON n.oid = c.relnamespace
        WHERE n.nspname = @schema AND c.relkind = 'v'
          AND NOT EXISTS (SELECT 1 FROM pg_depend d
                          WHERE d.classid = 'pg_class'::regclass AND d.objid = c.oid AND d.deptype = 'e')
        ORDER BY 1
        """;

    private const string RoutinesSql = """
        SELECT p.proname, p.prokind = 'p', pg_get_function_identity_arguments(p.oid), pg_get_functiondef(p.oid)
        FROM pg_proc p
        JOIN pg_namespace n ON n.oid = p.pronamespace
        WHERE n.nspname = @schema AND p.prokind IN ('f', 'p')
          AND NOT EXISTS (SELECT 1 FROM pg_depend d
                          WHERE d.classid = 'pg_proc'::regclass AND d.objid = p.oid AND d.deptype = 'e')
        ORDER BY 1, 3
        """;

    private const string TriggersSql = """
        SELECT c.relname, t.tgname, pg_get_triggerdef(t.oid, true)
        FROM pg_trigger t
        JOIN pg_class c ON c.oid = t.tgrelid
        JOIN pg_namespace n ON n.oid = c.relnamespace
        WHERE n.nspname = @schema AND NOT t.tgisinternal
        ORDER BY 1, 2
        """;

    public static async Task<PostgresProgrammableDefinitions> ReadAsync(
        string connectionString, string schema, CancellationToken cancellationToken)
    {
        // The connection's own search path stays in force: the engine qualifies a name in
        // these renderings only where that path would not find it, so the files read the way
        // the same connection's apply will run them.
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        var views = new List<PostgresViewDefinition>();
        await foreach (var row in RowsAsync(connection, ViewsSql, schema, cancellationToken))
        {
            views.Add(new PostgresViewDefinition(schema, row.GetString(0), row.GetString(1),
                row.IsDBNull(2) ? null : row.GetString(2)));
        }

        var routines = new List<PostgresRoutineDefinition>();
        await foreach (var row in RowsAsync(connection, RoutinesSql, schema, cancellationToken))
        {
            routines.Add(new PostgresRoutineDefinition(schema, row.GetString(0), row.GetBoolean(1),
                row.GetString(2), row.GetString(3)));
        }

        var triggers = new List<PostgresTriggerDefinition>();
        await foreach (var row in RowsAsync(connection, TriggersSql, schema, cancellationToken))
        {
            triggers.Add(new PostgresTriggerDefinition(schema, row.GetString(0), row.GetString(1), row.GetString(2)));
        }

        return new PostgresProgrammableDefinitions(views, routines, triggers);
    }

    private static async IAsyncEnumerable<NpgsqlDataReader> RowsAsync(
        NpgsqlConnection connection, string sql, string schema,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("schema", schema);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) yield return reader;
    }
}
