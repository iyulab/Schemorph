using Npgsql;
using Schemorph.Core.Ledger;

namespace Schemorph.Provider.Postgres;

/// <summary>
/// Tool-owned transactional execution — the control ADR-0007 chose native
/// execution to get: the script and its ledger rows commit in ONE transaction
/// (ADR-0004 §2 — either the script ran and is recorded, or neither
/// happened), and because Schemorph holds the boundary, the future apply can
/// join its plan-hash re-verification into the same unit. This is what makes
/// `atomicity: transactional` an earnable claim rather than an observation.
///
/// Internal until the provider surface declares the capability it belongs to:
/// the declared/refused symmetry (§2 of the dev plan) flips per slice, never
/// per helper.
/// </summary>
internal static class PgScriptExecutor
{
    public static async Task ExecuteAsync(
        string connectionString, string script,
        IReadOnlyList<LedgerEntry> ledgerEntries,
        CancellationToken cancellationToken = default)
    {
        var schema = PostgresProvider.TargetSchemaOf(connectionString);
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var command = new NpgsqlCommand(script, connection, transaction))
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var entry in ledgerEntries)
        {
            await PgLedgerSql.InsertAsync(connection, transaction, schema, entry, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }
}
