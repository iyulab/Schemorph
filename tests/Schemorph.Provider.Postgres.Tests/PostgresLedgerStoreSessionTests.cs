using Npgsql;
using Schemorph.Core.Ledger;

namespace Schemorph.Provider.Postgres.Tests;

public sealed class PostgresLedgerStoreSessionTests : IAsyncLifetime
{
    private PgTestSchema _schema = null!;
    private readonly PostgresProvider _provider = new();
    private readonly PostgresLedgerStore _ledger = new();
    private string _url = null!;

    public async Task InitializeAsync()
    {
        _schema = await PgTestSchema.CreateAsync("SELECT 1;");
        _url = new NpgsqlConnectionStringBuilder(PgTestSchema.ServerUrl!) { SearchPath = _schema.Name }
            .ConnectionString;
        await _ledger.EnsureInitializedAsync(_url);   // always its own connection, durable immediately
    }

    public async Task DisposeAsync() => await _schema.DisposeAsync();

    [SkippableFact]
    public async Task Appended_entries_are_rolled_back_with_the_session()
    {
        var session = await _provider.BeginApplySessionAsync(_url);

        await _ledger.AppendAsync(_url,
            new[] { new LedgerEntry("declarative", "dbo.Widget", "Alter", null, true, null) }, session);

        await session!.RollbackAsync();
        await session.DisposeAsync();

        var recorded = await _ledger.ReadAsync(_url, "declarative");
        Assert.Empty(recorded);
    }

    [SkippableFact]
    public async Task Appended_entries_are_durable_once_the_session_commits()
    {
        var session = await _provider.BeginApplySessionAsync(_url);

        await _ledger.AppendAsync(_url,
            new[] { new LedgerEntry("declarative", "dbo.Widget", "Alter", null, true, null) }, session);

        await session!.CommitAsync();
        await session.DisposeAsync();

        var recorded = await _ledger.ReadAsync(_url, "declarative");
        Assert.Single(recorded, e => e.ObjectName == "dbo.Widget");
    }
}
