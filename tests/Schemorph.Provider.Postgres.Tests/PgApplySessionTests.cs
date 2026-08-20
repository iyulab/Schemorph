using Npgsql;

namespace Schemorph.Provider.Postgres.Tests;

public sealed class PgApplySessionTests : IAsyncLifetime
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
    public async Task Commit_makes_work_done_through_the_session_durable()
    {
        var session = await _provider.BeginApplySessionAsync(_url);
        Assert.NotNull(session);
        var pg = PgApplySession.From(session!);

        await using (var command = new NpgsqlCommand(
            $"CREATE TABLE \"{_schema.Name}\".\"Sentinel\" (\"Id\" int)", pg.Connection, pg.Transaction))
        {
            await command.ExecuteNonQueryAsync();
        }
        await session!.CommitAsync();
        await session.DisposeAsync();

        Assert.True(await TableExistsAsync());
    }

    [SkippableFact]
    public async Task Rollback_discards_work_done_through_the_session()
    {
        var session = await _provider.BeginApplySessionAsync(_url);
        var pg = PgApplySession.From(session!);

        await using (var command = new NpgsqlCommand(
            $"CREATE TABLE \"{_schema.Name}\".\"Sentinel\" (\"Id\" int)", pg.Connection, pg.Transaction))
        {
            await command.ExecuteNonQueryAsync();
        }
        await session!.RollbackAsync();
        await session.DisposeAsync();

        Assert.False(await TableExistsAsync());
    }

    [SkippableFact]
    public async Task Disposing_without_commit_or_rollback_discards_the_work()
    {
        var session = await _provider.BeginApplySessionAsync(_url);
        var pg = PgApplySession.From(session!);

        await using (var command = new NpgsqlCommand(
            $"CREATE TABLE \"{_schema.Name}\".\"Sentinel\" (\"Id\" int)", pg.Connection, pg.Transaction))
        {
            await command.ExecuteNonQueryAsync();
        }
        await session!.DisposeAsync();

        Assert.False(await TableExistsAsync());
    }

    private async Task<bool> TableExistsAsync()
    {
        await using var connection = new NpgsqlConnection(PgTestSchema.ServerUrl);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT to_regclass(@name)::text", connection);
        command.Parameters.AddWithValue("name", $"\"{_schema.Name}\".\"Sentinel\"");
        return await command.ExecuteScalarAsync() is not (null or DBNull);
    }
}
