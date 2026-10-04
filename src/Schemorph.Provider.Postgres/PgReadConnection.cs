using Npgsql;
using Schemorph.Core.Providers;

namespace Schemorph.Provider.Postgres;

/// <summary>
/// The connection a live read runs on: its own, opened and closed here — or the open session's,
/// borrowed, so the read sees what that session has already done.
/// </summary>
/// <remarks>
/// <para>
/// A session's work is invisible to any other connection until it commits. A comparison that runs
/// after the session has executed something — a pre-stage script renaming a table — therefore has
/// to read through the session itself, or it plans against a schema that is about to stop existing.
/// Reading on a separate connection can also stall outright: a relation the session altered is
/// locked until it ends, and the caller holding the session is the one waiting for the read.
/// </para>
/// <para>
/// A borrowed connection is inside the session's transaction, so whatever a read changes about it
/// has to end with that transaction rather than leak into the pool: the search path is set
/// <c>LOCAL</c>, and a probe that must be undone runs under a savepoint instead of a transaction of
/// its own (the connection already has one).
/// </para>
/// </remarks>
internal sealed class PgReadConnection : IAsyncDisposable
{
    private const string ProbeSavepoint = "schemorph_probe";

    private readonly PgApplySession? _session;

    private PgReadConnection(NpgsqlConnection connection, PgApplySession? session)
    {
        Connection = connection;
        _session = session;
    }

    public NpgsqlConnection Connection { get; }

    public static async Task<PgReadConnection> OpenAsync(
        string connectionString, IApplySession? session, CancellationToken cancellationToken)
    {
        if (session is not null)
        {
            var pg = PgApplySession.From(session);
            return new PgReadConnection(pg.Connection, pg);
        }
        var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        return new PgReadConnection(connection, session: null);
    }

    /// <summary>Sets the search path for the reads that follow — for this transaction only on a borrowed connection.</summary>
    public async Task SetSearchPathAsync(CancellationToken cancellationToken, params string[] schemas)
    {
        var scope = _session is null ? "" : "LOCAL ";
        await using var command = new NpgsqlCommand(
            $"SET {scope}search_path TO {string.Join(", ", schemas.Select(DesiredStateRenderer.Quote))}", Connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Runs <paramref name="probe"/> and undoes whatever it did — a transaction of its own rolled back,
    /// or, on a borrowed connection, a savepoint rolled back to. The probe receives the transaction its
    /// commands must name.
    /// </summary>
    public async Task<T> ProbeAsync<T>(
        Func<NpgsqlTransaction, Task<T>> probe, CancellationToken cancellationToken)
    {
        if (_session is { } session)
        {
            await session.Transaction.SaveAsync(ProbeSavepoint, cancellationToken);
            try
            {
                return await probe(session.Transaction);
            }
            finally
            {
                await session.Transaction.RollbackAsync(ProbeSavepoint, cancellationToken);
                await session.Transaction.ReleaseAsync(ProbeSavepoint, cancellationToken);
            }
        }

        await using var transaction = await Connection.BeginTransactionAsync(cancellationToken);
        try
        {
            return await probe(transaction);
        }
        finally
        {
            await transaction.RollbackAsync(cancellationToken);
        }
    }

    /// <summary>Closes an owned connection; a borrowed one belongs to its session.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_session is null) await Connection.DisposeAsync();
    }
}
