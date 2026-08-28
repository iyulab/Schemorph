using Npgsql;

namespace Schemorph.Provider.Postgres.Tests;

/// <summary>
/// A throwaway schema per test, on the server named by SCHEMORPH_PG_TEST_URL.
/// A schema rather than a database: the baseline the provider must work under is
/// "DB owner only" (requirement R1), which does not include CREATEDB — the tests
/// have to live inside the same constraint the tool does.
/// </summary>
public sealed class PgTestSchema : IAsyncDisposable
{
    /// <summary>
    /// The env var, with pooling forced off. Every live test here builds its own connection
    /// string from a distinct schema name (via <see cref="Npgsql.NpgsqlConnectionStringBuilder.SearchPath"/>),
    /// so Npgsql — which pools per exact connection string — opens a brand new pool per test
    /// method and never closes its physical connection within one short `dotnet test` run
    /// (default pool idle lifetime is 300s). Confirmed empirically
    /// (issues/ISSUE-Schemorph-20260828-postgres-indexplanningtests-flaky-under-repeated-runs.md
    /// §6): with pooling on, a full run of this project accumulates up to ~86-97 concurrent
    /// server connections against a 97-connection non-superuser limit; with it off, peak
    /// concurrent connections dropped to 2. Forcing it off here — the one place every live test
    /// gets its server URL from — makes every "closed" connection a real one.
    /// </summary>
    public static string? ServerUrl
    {
        get
        {
            var raw = Environment.GetEnvironmentVariable("SCHEMORPH_PG_TEST_URL");
            return raw is null ? null : new NpgsqlConnectionStringBuilder(raw) { Pooling = false }.ConnectionString;
        }
    }

    private PgTestSchema(string name) => Name = name;

    public string Name { get; }

    public static async Task<PgTestSchema> CreateAsync(string ddl)
    {
        Skip.If(ServerUrl is null, "SCHEMORPH_PG_TEST_URL is not set; Postgres tests need a live server.");

        var name = "schemorph_test_" + Guid.NewGuid().ToString("n")[..12];
        await using (var connection = new NpgsqlConnection(ServerUrl))
        {
            await connection.OpenAsync();
            await Execute(connection, $"CREATE SCHEMA \"{name}\"");
            await Execute(connection, $"SET search_path TO \"{name}\"; {ddl}");
        }
        return new PgTestSchema(name);
    }

    public async ValueTask DisposeAsync()
    {
        if (ServerUrl is null) return;
        await using var connection = new NpgsqlConnection(ServerUrl);
        await connection.OpenAsync();
        await Execute(connection, $"DROP SCHEMA IF EXISTS \"{Name}\" CASCADE");
    }

    /// <summary>Runs raw SQL on the test server — used to prove rendered output executes.</summary>
    public static async Task ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(ServerUrl);
        await connection.OpenAsync();
        await Execute(connection, sql);
    }

    private static async Task Execute(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
