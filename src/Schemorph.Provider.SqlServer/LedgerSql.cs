using Microsoft.Data.SqlClient;
using Schemorph.Core.Ledger;

namespace Schemorph.Provider.SqlServer;

/// <summary>
/// The one place that knows the ledger INSERT dialect. Shared between the ledger
/// store (standalone appends) and the provider (appends that must commit inside a
/// script's transaction, ADR-0004).
/// </summary>
internal static class LedgerSql
{
    public static string AppliedBy => $"{Environment.UserName}@{Environment.MachineName}";

    public static async Task InsertAsync(
        SqlConnection connection, SqlTransaction? transaction, LedgerEntry entry, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand($"""
            INSERT dbo.{LedgerObjects.HistoryTable}
                (Kind, ObjectName, Operation, Checksum, AppliedBy, Succeeded, Detail)
            VALUES (@kind, @objectName, @operation, @checksum, @appliedBy, @succeeded, @detail);
            """, connection, transaction);
        command.Parameters.AddWithValue("@kind", entry.Kind);
        command.Parameters.AddWithValue("@objectName", entry.ObjectName);
        command.Parameters.AddWithValue("@operation", entry.Operation);
        command.Parameters.AddWithValue("@checksum", (object?)entry.Checksum ?? DBNull.Value);
        command.Parameters.AddWithValue("@appliedBy", AppliedBy);
        command.Parameters.AddWithValue("@succeeded", entry.Succeeded);
        command.Parameters.AddWithValue("@detail", (object?)entry.Detail ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
