using System.Diagnostics;
using Npgsql;
using Schemorph.Core.Operations;
using Schemorph.Core.Providers;

namespace Schemorph.Provider.Postgres.Tests;

/// <summary>
/// The opt-in lock timeout on a real server: that it reaches the apply's one transaction and
/// only that transaction, and that a statement stuck behind another session's lock fails fast,
/// rolls the whole apply back, and says so — where, without the option, it would wait for as
/// long as the other session holds on.
/// </summary>
public sealed class PostgresLockTimeoutTests : IAsyncLifetime
{
    private PostgresTestSchema _live = null!;
    private string _url = null!;
    private string _schemaDir = null!;
    private readonly PostgresProvider _provider = new();
    private readonly PostgresLedgerStore _ledger = new();

    private const string LiveDdl = """
        CREATE TABLE "Doc" (
            "Id" integer NOT NULL,
            "Name" text,
            CONSTRAINT "PK_Doc" PRIMARY KEY ("Id")
        );
        INSERT INTO "Doc" ("Id", "Name") VALUES (1, 'a');
        """;

    public async Task InitializeAsync()
    {
        _live = await PostgresTestSchema.CreateAsync(LiveDdl);
        _url = new NpgsqlConnectionStringBuilder(PostgresTestSchema.ServerUrl!) { SearchPath = _live.Name }.ConnectionString;
        _schemaDir = await WriteDesiredStateAsync(_live.Name);
    }

    /// <summary>The live table plus one new column: the apply's publish stage has to ALTER "Doc".</summary>
    private static async Task<string> WriteDesiredStateAsync(string schema)
    {
        var dir = Path.Combine(Path.GetTempPath(), "schemorph-pg-locktimeout-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(Path.Combine(dir, "tables"));
        await File.WriteAllTextAsync(Path.Combine(dir, "tables", "schema.sql"), $"""
            CREATE TABLE "{schema}"."Doc" (
                "Id" integer NOT NULL,
                "Name" text,
                "Note" text,
                CONSTRAINT "PK_Doc" PRIMARY KEY ("Id")
            );
            """);
        return dir;
    }

    public async Task DisposeAsync()
    {
        await _live.DisposeAsync();
        try { Directory.Delete(_schemaDir, recursive: true); } catch { }
    }

    private static async Task<string> ShowLockTimeoutAsync(PostgresApplySession session)
    {
        await using var command = new NpgsqlCommand("SHOW lock_timeout", session.Connection, session.Transaction);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    [SkippableFact]
    public async Task Without_the_option_the_session_keeps_the_servers_own_setting()
    {
        Skip.If(PostgresTestSchema.ServerUrl is null, "SCHEMORPH_PG_TEST_URL is not set; Postgres tests need a live server.");

        await using var session = PostgresApplySession.From((await _provider.BeginApplySessionAsync(_url))!);

        Assert.Equal("0", await ShowLockTimeoutAsync(session));
    }

    [SkippableTheory]
    [InlineData(500, "500ms")]
    [InlineData(30_000, "30s")]
    [InlineData(120_000, "2min")]
    public async Task With_the_option_the_transaction_carries_the_timeout_and_the_setting_ends_with_it(
        int milliseconds, string shownByServer)
    {
        Skip.If(PostgresTestSchema.ServerUrl is null, "SCHEMORPH_PG_TEST_URL is not set; Postgres tests need a live server.");

        var session = PostgresApplySession.From((await _provider.BeginApplySessionAsync(
            _url, new ApplySessionOptions(TimeSpan.FromMilliseconds(milliseconds))))!);
        await using (session)
        {
            Assert.Equal(shownByServer, await ShowLockTimeoutAsync(session));

            await session.CommitAsync();

            // The same physical connection, after the commit: the setting was local to the
            // transaction and must not have leaked into whatever uses this connection next.
            await using var after = new NpgsqlCommand("SHOW lock_timeout", session.Connection);
            Assert.Equal("0", (string)(await after.ExecuteScalarAsync())!);
        }
    }

    [SkippableFact]
    public async Task A_statement_stuck_behind_another_sessions_lock_fails_fast_and_nothing_is_applied()
    {
        Skip.If(PostgresTestSchema.ServerUrl is null, "SCHEMORPH_PG_TEST_URL is not set; Postgres tests need a live server.");

        // Warm the provider on a schema nobody holds, so the timing below measures the wait and not
        // the first apply of a cold process (which alone takes many seconds on this machine).
        await using (var warm = await PostgresTestSchema.CreateAsync(LiveDdl))
        {
            var warmDir = await WriteDesiredStateAsync(warm.Name);
            try
            {
                var warmUrl = new NpgsqlConnectionStringBuilder(PostgresTestSchema.ServerUrl!) { SearchPath = warm.Name }.ConnectionString;
                var warmup = await ApplyOperation.RunAsync(_provider, _ledger, new ApplyOperation.Request(warmDir, warmUrl));
                Assert.True(warmup.Success, string.Join("; ", warmup.Errors.Select(e => $"{e.Code}: {e.Text}")));
            }
            finally { try { Directory.Delete(warmDir, recursive: true); } catch { } }
        }

        // Another session holds the table: the ALTER the publish needs cannot get its lock.
        await using var holder = new NpgsqlConnection(PostgresTestSchema.ServerUrl);
        await holder.OpenAsync();
        await using var holderTransaction = await holder.BeginTransactionAsync();
        await using (var lockCommand = new NpgsqlCommand(
            $"LOCK TABLE \"{_live.Name}\".\"Doc\" IN ACCESS EXCLUSIVE MODE", holder, holderTransaction))
        {
            await lockCommand.ExecuteNonQueryAsync();
        }

        // The ceiling is the safety net for a regression: with no timeout in force the apply would
        // wait on the holder indefinitely, and a hung test is a worse failure than a red one.
        using var ceiling = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var stopwatch = Stopwatch.StartNew();
        TimeSpan? planAnnouncedAt = null;
        var outcome = await ApplyOperation.RunAsync(
            _provider, _ledger,
            new ApplyOperation.Request(_schemaDir, _url, LockTimeout: TimeSpan.FromMilliseconds(600)),
            onPlan: _ => planAnnouncedAt = stopwatch.Elapsed,
            cancellationToken: ceiling.Token);
        stopwatch.Stop();

        Assert.False(outcome.Success);
        var message = Assert.Single(outcome.Errors);
        Assert.Equal("55P03", message.Code);                      // the engine's own code
        Assert.Contains("lock timeout", message.Text);            // the curated hint, not a bare engine string
        Assert.Contains("nothing was applied", message.Text);
        // From the plan being announced (everything after is execution) to the failure: it waited for
        // the timeout — not failing for some other reason up front — and not much longer. Without the
        // timeout in force it would have waited on the holder until the ceiling above.
        Assert.NotNull(planAnnouncedAt);
        Assert.InRange(stopwatch.Elapsed - planAnnouncedAt!.Value, TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(10));

        // Nothing landed: the column is not there, even though the holder never touched it.
        await holderTransaction.RollbackAsync();
        Assert.False(await ColumnExistsAsync("Note"));

        // And the failure is recoverable the way every apply failure is — run it again once the
        // other session is done, with no timeout this time.
        var rerun = await ApplyOperation.RunAsync(_provider, _ledger, new ApplyOperation.Request(_schemaDir, _url));
        Assert.True(rerun.Success, string.Join("; ", rerun.Errors.Select(e => $"{e.Code}: {e.Text}")));
        Assert.True(await ColumnExistsAsync("Note"));
    }

    private async Task<bool> ColumnExistsAsync(string column)
    {
        await using var connection = new NpgsqlConnection(PostgresTestSchema.ServerUrl);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM information_schema.columns WHERE table_schema = @s AND table_name = 'Doc' AND column_name = @c",
            connection);
        command.Parameters.AddWithValue("s", _live.Name);
        command.Parameters.AddWithValue("c", column);
        return (long)(await command.ExecuteScalarAsync())! == 1;
    }
}
