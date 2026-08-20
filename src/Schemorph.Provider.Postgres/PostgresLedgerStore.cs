using Npgsql;
using Schemorph.Core.Ledger;
using Schemorph.Core.Providers;

namespace Schemorph.Provider.Postgres;

/// <summary>
/// Postgres dialect implementation of the core-defined ledger contract. The
/// table shape mirrors <see cref="LedgerEntry"/>; semantics live in the core.
/// The ledger sits in the connection's target schema (same resolution the
/// provider uses — <see cref="PostgresProvider.TargetSchemaOf"/>).
/// </summary>
public sealed class PostgresLedgerStore : ILedgerStore
{
    /// <summary>
    /// Deliberately never joins <paramref name="session"/> even when given
    /// one — the ledger table's existence must survive independently of
    /// whatever the apply that opened the session does next (ADR-0004
    /// decision 4's failure row depends on this table already being durably
    /// there — see the plan doc's addendum). Always its own connection, same
    /// as before <see cref="IApplySession"/> existed.
    /// </summary>
    public async Task EnsureInitializedAsync(string connectionString, IApplySession? session = null, CancellationToken cancellationToken = default)
    {
        var schema = PostgresProvider.TargetSchemaOf(connectionString);
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        // The apply initializes the ledger BEFORE anything runs (ADR-0004), and
        // on a fresh database the target schema may not exist yet either.
        await using (var schemaCommand = new NpgsqlCommand(
            $"CREATE SCHEMA IF NOT EXISTS {DesiredStateRenderer.Quote(schema)}", connection))
        {
            await schemaCommand.ExecuteNonQueryAsync(cancellationToken);
        }
        await using var command = new NpgsqlCommand(PgLedgerSql.CreateTableSql(schema), connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task AppendAsync(string connectionString, IReadOnlyList<LedgerEntry> entries, IApplySession? session = null, CancellationToken cancellationToken = default)
    {
        if (entries.Count == 0) return;

        var schema = PostgresProvider.TargetSchemaOf(connectionString);

        if (session is not null)
        {
            var pg = PgApplySession.From(session);
            foreach (var entry in entries)
            {
                await PgLedgerSql.InsertAsync(pg.Connection, pg.Transaction, schema, entry, cancellationToken);
            }
            return;
        }

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        foreach (var entry in entries)
        {
            await PgLedgerSql.InsertAsync(connection, transaction: null, schema, entry, cancellationToken);
        }
    }

    public async Task<IReadOnlyList<LedgerEntry>> ReadAsync(string connectionString, string kind, CancellationToken cancellationToken = default)
    {
        var schema = PostgresProvider.TargetSchemaOf(connectionString);
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        // The ledger may not exist yet on a fresh database — that means "no history".
        await using var existsCommand = new NpgsqlCommand(
            "SELECT to_regclass(@name)::text", connection);
        existsCommand.Parameters.AddWithValue("name", PgLedgerSql.QualifiedTable(schema));
        if (await existsCommand.ExecuteScalarAsync(cancellationToken) is DBNull or null)
        {
            return Array.Empty<LedgerEntry>();
        }

        await using var command = new NpgsqlCommand($"""
            SELECT "Kind", "ObjectName", "Operation", "Checksum", "Succeeded", "Detail", "AppliedAtUtc"
            FROM {PgLedgerSql.QualifiedTable(schema)}
            WHERE "Kind" = @kind
            ORDER BY "Id"
            """, connection);
        command.Parameters.AddWithValue("kind", kind);

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
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.GetDateTime(6)));
        }
        return entries;
    }
}
