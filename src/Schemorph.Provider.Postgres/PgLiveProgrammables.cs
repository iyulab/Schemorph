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
