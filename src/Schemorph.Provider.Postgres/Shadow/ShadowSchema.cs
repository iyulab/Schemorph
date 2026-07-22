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
    /// Retarget the whole desired-state set from <paramref name="sourceSchema"/>
    /// into this schema and execute it in dependency-safe statement order
    /// (<see cref="SchemaRewriter.RetargetSet"/>), all in one transaction — a
    /// desired state that half-applies is not a comparison side.
    ///
    /// After applying, CHECK constraints are re-added from their own catalog
    /// rendering (same transaction). CHECK expressions are the one P1 class
    /// whose rendering is not parse-stable — a varchar IN-list renders as
    /// <c>(ARRAY[…]::text[])</c> from the user's text but as per-element
    /// <c>(…)::text</c> casts once that rendering is parsed again. The live
    /// side always takes that second parse (apply executes DDL synthesized
    /// from shadow renderings), so the shadow must take it too, or the two
    /// sides speak different canonical texts forever and every varchar-CHECK
    /// table re-plans as an alter. One extra round-trip reaches the engine's
    /// fixed point (measured on PG 16); text-typed CHECKs are unaffected
    /// (already stable — no relabeling to erase).
    /// </summary>
    public async Task ApplyAsync(
        IReadOnlyList<string> sqlTexts, string sourceSchema, CancellationToken cancellationToken = default)
    {
        var rewritten = SchemaRewriter.RetargetSet(sqlTexts, sourceSchema, Name);

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var command = new NpgsqlCommand(rewritten, connection, transaction))
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await NormalizeCheckConstraintsAsync(connection, transaction, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task NormalizeCheckConstraintsAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        const string readChecks = """
            SELECT c.relname, con.conname, pg_get_constraintdef(con.oid)
            FROM pg_constraint con
            JOIN pg_class c ON c.oid = con.conrelid
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = @schema AND con.contype = 'c'
            ORDER BY c.relname, con.conname
            """;

        var readds = new List<string>();
        await using (var read = new NpgsqlCommand(readChecks, connection, transaction))
        {
            read.Parameters.AddWithValue("schema", Name);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var table = $"{DesiredStateRenderer.Quote(Name)}.{DesiredStateRenderer.Quote(reader.GetString(0))}";
                var constraint = DesiredStateRenderer.Quote(reader.GetString(1));
                var definition = reader.GetString(2);
                readds.Add(
                    $"ALTER TABLE {table} DROP CONSTRAINT {constraint};\n" +
                    $"ALTER TABLE {table} ADD CONSTRAINT {constraint} {definition};");
            }
        }
        if (readds.Count == 0) return;

        await using var normalize = new NpgsqlCommand(
            string.Join("\n", readds), connection, transaction);
        await normalize.ExecuteNonQueryAsync(cancellationToken);
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
