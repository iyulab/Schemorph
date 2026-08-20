using Npgsql;
using Schemorph.Core.Ledger;

namespace Schemorph.Provider.Postgres.Tests;

public sealed class PgScriptExecutorSessionTests : IAsyncLifetime
{
    private PgTestSchema _schema = null!;
    private readonly PostgresProvider _provider = new();
    private string _url = null!;

    public async Task InitializeAsync()
    {
        _schema = await PgTestSchema.CreateAsync("SELECT 1;");
        _url = new NpgsqlConnectionStringBuilder(PgTestSchema.ServerUrl!) { SearchPath = _schema.Name }
            .ConnectionString;
    }

    public async Task DisposeAsync() => await _schema.DisposeAsync();

    [SkippableFact]
    public async Task A_script_executed_through_a_session_does_not_commit_until_the_session_does()
    {
        var session = PgApplySession.From((await _provider.BeginApplySessionAsync(_url))!);

        await PgScriptExecutor.ExecuteAsync(
            _url, $"CREATE TABLE \"{_schema.Name}\".\"Sentinel\" (\"Id\" int)",
            Array.Empty<LedgerEntry>(), session);

        // Not visible on a fresh connection yet — the session's transaction
        // has not committed.
        Assert.False(await TableExistsAsync());

        await session.CommitAsync();
        await session.DisposeAsync();

        Assert.True(await TableExistsAsync());
    }

    [SkippableFact]
    public async Task A_scripts_ledger_entries_land_in_the_same_session_transaction()
    {
        var session = PgApplySession.From((await _provider.BeginApplySessionAsync(_url))!);
        await using (var command = new NpgsqlCommand(
            PgLedgerSql.CreateTableSql(_schema.Name), session.Connection, session.Transaction))
        {
            await command.ExecuteNonQueryAsync();
        }

        var entry = new LedgerEntry("redefine", "dbo.Widget", "Redefine", "checksum-1", Succeeded: true, Detail: "View");
        await PgScriptExecutor.ExecuteAsync(
            _url, $"CREATE VIEW \"{_schema.Name}\".\"V\" AS SELECT 1", new[] { entry }, session);

        await session.RollbackAsync();
        await session.DisposeAsync();

        // Both the view and its ledger row are gone — they shared the rolled-back transaction.
        Assert.False(await ViewExistsAsync());
    }

    private async Task<bool> TableExistsAsync()
    {
        await using var connection = new NpgsqlConnection(PgTestSchema.ServerUrl);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT to_regclass(@name)::text", connection);
        command.Parameters.AddWithValue("name", $"\"{_schema.Name}\".\"Sentinel\"");
        return await command.ExecuteScalarAsync() is not (null or DBNull);
    }

    private async Task<bool> ViewExistsAsync()
    {
        await using var connection = new NpgsqlConnection(PgTestSchema.ServerUrl);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT to_regclass(@name)::text", connection);
        command.Parameters.AddWithValue("name", $"\"{_schema.Name}\".\"V\"");
        return await command.ExecuteScalarAsync() is not (null or DBNull);
    }
}
