using Npgsql;
using Schemorph.Core.Ledger;

namespace Schemorph.Provider.Postgres;

/// <summary>
/// The one place that knows the Postgres ledger dialect. Shared between the
/// ledger store (standalone appends) and the script executor (appends that
/// must commit inside the script's own transaction, ADR-0004 §2).
///
/// The table lives in the connection's target schema, not a fixed one: the R1
/// baseline is a DB owner whose rights may end at their schema, so Schemorph's
/// bookkeeping stays inside the schema it manages.
/// </summary>
internal static class PgLedgerSql
{
    public static string AppliedBy => $"{Environment.UserName}@{Environment.MachineName}";

    public static string QualifiedTable(string schema)
        => $"{DesiredStateRenderer.Quote(schema)}.{DesiredStateRenderer.Quote(LedgerObjects.HistoryTable)}";

    public static string CreateTableSql(string schema) => $"""
        CREATE TABLE IF NOT EXISTS {QualifiedTable(schema)}
        (
            "Id"           bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            "AppliedAtUtc" timestamptz NOT NULL DEFAULT now(),
            "Kind"         text    NOT NULL,
            "ObjectName"   text    NOT NULL,
            "Operation"    text    NOT NULL,
            "Checksum"     text,
            "AppliedBy"    text    NOT NULL,
            "Succeeded"    boolean NOT NULL,
            "Detail"       text
        )
        """;

    public static async Task InsertAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, string schema,
        LedgerEntry entry, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            INSERT INTO {QualifiedTable(schema)}
                ("Kind", "ObjectName", "Operation", "Checksum", "AppliedBy", "Succeeded", "Detail")
            VALUES (@kind, @objectName, @operation, @checksum, @appliedBy, @succeeded, @detail)
            """, connection, transaction);
        command.Parameters.AddWithValue("kind", entry.Kind);
        command.Parameters.AddWithValue("objectName", entry.ObjectName);
        command.Parameters.AddWithValue("operation", entry.Operation);
        command.Parameters.AddWithValue("checksum", (object?)entry.Checksum ?? DBNull.Value);
        command.Parameters.AddWithValue("appliedBy", AppliedBy);
        command.Parameters.AddWithValue("succeeded", entry.Succeeded);
        command.Parameters.AddWithValue("detail", (object?)entry.Detail ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
