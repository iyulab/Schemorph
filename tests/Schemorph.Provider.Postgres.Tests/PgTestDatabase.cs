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
    public static string? ServerUrl => Environment.GetEnvironmentVariable("SCHEMORPH_PG_TEST_URL");

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
