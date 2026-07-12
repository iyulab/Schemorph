using Microsoft.Data.SqlClient;
using Schemorph.Core.Ledger;

namespace Schemorph.Provider.SqlServer;

/// <summary>
/// SQL Server dialect implementation of the core-defined ledger contract.
/// The table shape mirrors <see cref="LedgerEntry"/>; semantics live in the core.
/// </summary>
public sealed class SqlServerLedgerStore : ILedgerStore
{
    public async Task EnsureInitializedAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand($"""
            IF OBJECT_ID('dbo.{LedgerObjects.HistoryTable}') IS NULL
            CREATE TABLE dbo.{LedgerObjects.HistoryTable}
            (
                Id           INT IDENTITY (1, 1) NOT NULL
                    CONSTRAINT PK_{LedgerObjects.HistoryTable} PRIMARY KEY,
                AppliedAtUtc DATETIME2(3)   NOT NULL
                    CONSTRAINT DF_{LedgerObjects.HistoryTable}_AppliedAtUtc DEFAULT SYSUTCDATETIME(),
                Kind         NVARCHAR(20)   NOT NULL,
                ObjectName   NVARCHAR(512)  NOT NULL,
                Operation    NVARCHAR(20)   NOT NULL,
                Checksum     NVARCHAR(128)  NULL,
                AppliedBy    NVARCHAR(128)  NOT NULL,
                Succeeded    BIT            NOT NULL,
                Detail       NVARCHAR(MAX)  NULL
            );
            """, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task AppendAsync(string connectionString, IReadOnlyList<LedgerEntry> entries, CancellationToken cancellationToken = default)
    {
        if (entries.Count == 0) return;

        var appliedBy = $"{Environment.UserName}@{Environment.MachineName}";
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        foreach (var entry in entries)
        {
            await using var command = new SqlCommand($"""
                INSERT dbo.{LedgerObjects.HistoryTable}
                    (Kind, ObjectName, Operation, Checksum, AppliedBy, Succeeded, Detail)
                VALUES (@kind, @objectName, @operation, @checksum, @appliedBy, @succeeded, @detail);
                """, connection);
            command.Parameters.AddWithValue("@kind", entry.Kind);
            command.Parameters.AddWithValue("@objectName", entry.ObjectName);
            command.Parameters.AddWithValue("@operation", entry.Operation);
            command.Parameters.AddWithValue("@checksum", (object?)entry.Checksum ?? DBNull.Value);
            command.Parameters.AddWithValue("@appliedBy", appliedBy);
            command.Parameters.AddWithValue("@succeeded", entry.Succeeded);
            command.Parameters.AddWithValue("@detail", (object?)entry.Detail ?? DBNull.Value);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    public async Task<IReadOnlyList<LedgerEntry>> ReadAsync(string connectionString, string kind, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        // The ledger may not exist yet on a fresh database — that means "no history".
        await using var existsCommand = new SqlCommand(
            $"SELECT OBJECT_ID('dbo.{LedgerObjects.HistoryTable}')", connection);
        if (await existsCommand.ExecuteScalarAsync(cancellationToken) is DBNull or null)
        {
            return Array.Empty<LedgerEntry>();
        }

        await using var command = new SqlCommand($"""
            SELECT Kind, ObjectName, Operation, Checksum, Succeeded, Detail
            FROM dbo.{LedgerObjects.HistoryTable}
            WHERE Kind = @kind
            ORDER BY Id;
            """, connection);
        command.Parameters.AddWithValue("@kind", kind);

        var entries = new List<LedgerEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            entries.Add(new LedgerEntry(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetBoolean(4),
                reader.IsDBNull(5) ? null : reader.GetString(5)));
        }
        return entries;
    }
}
