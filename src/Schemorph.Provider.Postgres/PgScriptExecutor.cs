using Npgsql;
using Schemorph.Core.Ledger;

namespace Schemorph.Provider.Postgres;

/// <summary>
/// Tool-owned transactional execution — the control ADR-0007 chose native
/// execution to get: the script and its ledger rows commit in ONE transaction
/// (ADR-0004 §2 — either the script ran and is recorded, or neither
/// happened). That guarantee is real but scoped to this one call: every
/// caller (the declarative publish, and each redefine/migration object) opens
/// its own connection here, so the guarantee is per-call, not a boundary
/// shared across calls — this alone does not earn `atomicity: transactional`
/// for the whole apply (ADR-0007's 2026-08-19 addendum). It is what makes
/// each individual stage's own atomicity real, which is what `partial` means.
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
