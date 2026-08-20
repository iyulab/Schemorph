using Npgsql;
using Schemorph.Core.Providers;

namespace Schemorph.Provider.Postgres;

/// <summary>
/// The PG half of ADR-0004's declared apply-unit transaction: one connection,
/// one transaction, held open across every execution call the apply makes.
/// Internal — <see cref="IApplySession"/> is all the core ever sees; only this
/// project reaches for <see cref="Connection"/>/<see cref="Transaction"/> to
/// hand the SAME boundary to <see cref="PgScriptExecutor"/> and
/// <see cref="PostgresLedgerStore"/>.
/// </summary>
internal sealed class PgApplySession : IApplySession
{
    private bool _completed;

    private PgApplySession(NpgsqlConnection connection, NpgsqlTransaction transaction, string connectionString)
    {
        Connection = connection;
        Transaction = transaction;
        ConnectionString = connectionString;
    }

    public NpgsqlConnection Connection { get; }
    public NpgsqlTransaction Transaction { get; }

    /// <summary>
    /// The string this session was opened with, kept alongside the connection
    /// rather than read back off <see cref="Connection"/> — Npgsql is not
    /// guaranteed to echo it verbatim, and <c>TargetSchemaOf</c> needs exactly
    /// what was given.
    /// </summary>
    public string ConnectionString { get; }

    public static async Task<PgApplySession> OpenAsync(string connectionString, CancellationToken cancellationToken)
    {
        var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        var transaction = await connection.BeginTransactionAsync(cancellationToken);
        return new PgApplySession(connection, transaction, connectionString);
    }

    public async Task CommitAsync(CancellationToken cancellationToken = default)
    {
        await Transaction.CommitAsync(cancellationToken);
        _completed = true;
    }

    public async Task RollbackAsync(CancellationToken cancellationToken = default)
    {
        await Transaction.RollbackAsync(cancellationToken);
        _completed = true;
    }

    /// <summary>
    /// Rolls back if neither <see cref="CommitAsync"/> nor
    /// <see cref="RollbackAsync"/> ran first (e.g. a plan-hash mismatch, which
    /// aborts before anything executes) — standard ADO.NET transaction
    /// disposal semantics, made explicit here rather than left implicit.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (!_completed)
        {
            await Transaction.RollbackAsync();
        }
        await Transaction.DisposeAsync();
        await Connection.DisposeAsync();
    }

    /// <summary>Downcast with a real error — mirrors <see cref="PgDesiredState.From"/>: only this provider's own session ever reaches its own execution calls.</summary>
    public static PgApplySession From(IApplySession session) => session as PgApplySession
        ?? throw new ArgumentException(
            $"The apply session was not opened by this provider (got {session.GetType().Name}).", nameof(session));
}
