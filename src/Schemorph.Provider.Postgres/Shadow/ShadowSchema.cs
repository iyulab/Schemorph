using Npgsql;

namespace Schemorph.Provider.Postgres.Shadow;

/// <summary>
/// A scratch schema in the target database — the shadow side of ADR-0007's
/// normalization: desired-state DDL is applied here (retargeted by
/// <see cref="SchemaRewriter"/>), then read back through <c>pg_catalog</c> so
/// both comparison sides speak the engine's canonical rendering.
///
/// A schema rather than a database because the R1 baseline is a DB-owner
/// connection without <c>CREATEDB</c> — the scratch-schema variant IS the
/// primary path, not the fallback. Named uniquely per instance so concurrent
/// operations cannot collide, and dropped on disposal; cleanup failure never
/// replaces the operation's own outcome (the TemporaryWorkspace rule).
/// </summary>
internal sealed class ShadowSchema : IAsyncDisposable
{
    private readonly string _connectionString;

    public string Name { get; }

    private ShadowSchema(string connectionString, string name)
    {
        _connectionString = connectionString;
        Name = name;
    }

    public static async Task<ShadowSchema> CreateAsync(
        string connectionString, CancellationToken cancellationToken = default)
    {
        var name = $"schemorph_shadow_{Guid.NewGuid():N}"[..25];
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            $"CREATE SCHEMA {DesiredStateRenderer.Quote(name)}", connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
        return new ShadowSchema(connectionString, name);
    }

    /// <summary>
    /// Retarget one desired-state text from <paramref name="sourceSchema"/>
    /// into this schema and execute it, all statements in one transaction —
    /// a desired state that half-applies is not a comparison side.
    /// </summary>
    public async Task ApplyAsync(
        string sql, string sourceSchema, CancellationToken cancellationToken = default)
    {
        var rewritten = SchemaRewriter.Retarget(sql, sourceSchema, Name);

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(rewritten, connection, transaction);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(
                $"DROP SCHEMA IF EXISTS {DesiredStateRenderer.Quote(Name)} CASCADE", connection);
            await command.ExecuteNonQueryAsync();
        }
        catch
        {
            // Cleanup must never speak over the operation's own outcome. An
            // orphaned schemorph_shadow_* schema is visible and harmless; a
            // masked failure is neither.
        }
    }
}
