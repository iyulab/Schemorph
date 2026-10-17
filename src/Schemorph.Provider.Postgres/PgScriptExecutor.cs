using Npgsql;
using Schemorph.Core.Ledger;

namespace Schemorph.Provider.Postgres;

/// <summary>
/// Tool-owned transactional execution — the control ADR-0007 chose native
/// execution to get: the script and its ledger rows commit in ONE transaction
/// (ADR-0004 §2 — either the script ran and is recorded, or neither
/// happened). Given a <see cref="PgApplySession"/>, that boundary is the
/// session's own connection/transaction — shared across every call the whole
/// apply makes, and left uncommitted here; without one, this call opens and
/// commits its own connection, so the guarantee is per-call only. That is
/// what makes each individual stage's own atomicity real, which is what
/// `partial` means.
///
/// Internal until the provider surface declares the capability it belongs to:
/// the declared/refused symmetry (§2 of the dev plan) flips per slice, never
/// per helper.
/// </summary>
internal static class PgScriptExecutor
{
    public static Task ExecuteAsync(
        string connectionString, string script,
        IReadOnlyList<LedgerEntry> ledgerEntries,
        PgApplySession? session = null,
        CancellationToken cancellationToken = default,
        string? read = null)
        => session is null
            ? ExecuteOwnConnectionAsync(connectionString, script, ledgerEntries, read, cancellationToken)
            : ExecuteOnSessionAsync(session, script, ledgerEntries, read, cancellationToken);

    private static async Task ExecuteOwnConnectionAsync(
        string connectionString, string script,
        IReadOnlyList<LedgerEntry> ledgerEntries, string? read, CancellationToken cancellationToken)
    {
        var schema = PostgresProvider.TargetSchemaOf(connectionString);
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var command = new NpgsqlCommand(script, connection, transaction))
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await ReadAsync(read, connection, transaction, cancellationToken);

        foreach (var entry in ledgerEntries)
        {
            await PgLedgerSql.InsertAsync(connection, transaction, schema, entry, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// The session-scoped path: the SAME connection+transaction the whole
    /// apply shares, so this call's script and ledger rows land in the one
    /// boundary the apply-unit transaction promises. No commit here — the
    /// session commits once, at the end of the whole apply
    /// (<see cref="Operations.ApplyOperation.RunAsync"/> in Core).
    /// </summary>
    private static async Task ExecuteOnSessionAsync(
        PgApplySession session, string script,
        IReadOnlyList<LedgerEntry> ledgerEntries, string? read, CancellationToken cancellationToken)
    {
        var schema = PostgresProvider.TargetSchemaOf(session.ConnectionString);
        await using (var command = new NpgsqlCommand(script, session.Connection, session.Transaction))
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await ReadAsync(read, session.Connection, session.Transaction, cancellationToken);

        foreach (var entry in ledgerEntries)
        {
            await PgLedgerSql.InsertAsync(session.Connection, session.Transaction, schema, entry, cancellationToken);
        }
    }

    /// <summary>
    /// The read that follows a script inside its transaction, if any — a re-defined view read over the rows already
    /// there (<see cref="PostgresProvider.RedefineAsync"/>); its failure rolls the transaction back like the script's own.
    /// </summary>
    private static async Task ReadAsync(
        string? read, NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        if (read is null) return;
        await using var command = new NpgsqlCommand(read, connection, transaction);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
