using Microsoft.Data.SqlClient;

namespace Schemorph.IntegrationTests;

/// <summary>
/// One throwaway database per test class, on the server named by SCHEMORPH_TEST_URL
/// (a connection string; its Initial Catalog is ignored). Tests are skipped when the
/// variable is not set, so the unit-test loop stays database-free.
/// Local: point it at LocalDB. CI: the mssql service container.
/// </summary>
public sealed class TestDatabase : IDisposable
{
    private readonly string _serverConnectionString = null!;
    private readonly string _databaseName = null!;

    public static string? ServerUrl => Environment.GetEnvironmentVariable("SCHEMORPH_TEST_URL");

    /// <summary>Connection string to the throwaway database.</summary>
    public string Url { get; } = null!;

    public TestDatabase()
    {
        Skip.If(ServerUrl is null, "SCHEMORPH_TEST_URL is not set; integration tests need a SQL Server.");

        _databaseName = $"SchemorphIT_{Guid.NewGuid():N}";
        var builder = new SqlConnectionStringBuilder(ServerUrl) { InitialCatalog = "master" };
        _serverConnectionString = builder.ConnectionString;
        Execute(_serverConnectionString, $"CREATE DATABASE [{_databaseName}]");
        Url = new SqlConnectionStringBuilder(ServerUrl) { InitialCatalog = _databaseName }.ConnectionString;
    }

    public void Execute(string sql) => Execute(Url, sql);

    public T? Scalar<T>(string sql)
    {
        using var connection = new SqlConnection(Url);
        connection.Open();
        using var command = new SqlCommand(sql, connection);
        var value = command.ExecuteScalar();
        return value is null or DBNull ? default : (T)value;
    }

    private static void Execute(string connectionString, string sql)
    {
        using var connection = new SqlConnection(connectionString);
        connection.Open();
        using var command = new SqlCommand(sql, connection);
        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        if (_serverConnectionString is null) return;
        SqlConnection.ClearAllPools();   // active pooled connections block DROP
        Execute(_serverConnectionString,
            $"IF DB_ID('{_databaseName}') IS NOT NULL BEGIN ALTER DATABASE [{_databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_databaseName}]; END");
    }
}
