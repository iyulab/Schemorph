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

    /// <summary>
    /// One connection held open for the lifetime of the test process, so the
    /// server cannot decide the run is over while it is still going.
    ///
    /// LocalDB shuts itself down after a few idle minutes ("The RANU instance is
    /// terminating in response to its internal time out"), and a suite that runs
    /// serially leaves longer gaps between connections than one that does not —
    /// long enough, in a full run, that the instance went away mid-suite and every
    /// test after it failed on a connection timeout. That looks exactly like a
    /// product regression and is not one, which is the expensive kind of false
    /// signal. Pooling does not prevent it: pooled connections are closed, and an
    /// idle pool is what the server is counting. Only an open one holds the door.
    ///
    /// Never disposed on purpose — it ends with the process. Irrelevant against a
    /// real server (CI's container does not idle out); harmless there too.
    /// </summary>
    private static readonly Lazy<SqlConnection?> KeepAlive = new(() =>
    {
        if (ServerUrl is null) return null;
        var connection = new SqlConnection(
            new SqlConnectionStringBuilder(ServerUrl) { InitialCatalog = "master", Pooling = false }.ConnectionString);
        connection.Open();
        return connection;
    });

    /// <summary>Connection string to the throwaway database.</summary>
    public string Url { get; } = null!;

    public TestDatabase()
    {
        Skip.If(ServerUrl is null, "SCHEMORPH_TEST_URL is not set; integration tests need a SQL Server.");

        _ = KeepAlive.Value;   // first test class starts it; the process ends it

        _databaseName = $"SchemorphIT_{Guid.NewGuid():N}";
        var builder = new SqlConnectionStringBuilder(ServerUrl) { InitialCatalog = "master" };
        _serverConnectionString = builder.ConnectionString;
        Execute(_serverConnectionString, $"CREATE DATABASE [{_databaseName}]");
        Url = new SqlConnectionStringBuilder(ServerUrl) { InitialCatalog = _databaseName }.ConnectionString;
    }

    public void Execute(string sql) => Execute(Url, sql);

    /// <summary>Run against the server (master), for server-scoped objects such as logins.</summary>
    public void ExecuteOnServer(string sql) => Execute(_serverConnectionString, sql);

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
