using Npgsql;
using Schemorph.Core.Operations;
using Schemorph.Core.Providers;

namespace Schemorph.Provider.Postgres.Tests;

/// <summary>
/// The online stage (ADR-0009) on a real server: statements that cannot join the apply's transaction run
/// after it commits, each on its own. The property the whole feature rests on is decision 6 - a failure
/// in this stage leaves something the next plan sees, so running the same apply again converges. These
/// tests make the stage fail the way a real one does (a unique build over duplicate data, a validation over
/// violating rows, a lock another session holds) rather than by hand-crafting the leftover state.
/// </summary>
public sealed class PostgresOnlineStageTests : IAsyncLifetime
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
            "Qty" integer,
            CONSTRAINT "PK_Doc" PRIMARY KEY ("Id")
        );
        INSERT INTO "Doc" ("Id", "Name", "Qty") VALUES (1, 'a', 1), (2, 'b', 2);
        """;

    public async Task InitializeAsync()
    {
        _live = await PostgresTestSchema.CreateAsync(LiveDdl);
        _url = new NpgsqlConnectionStringBuilder(PostgresTestSchema.ServerUrl!) { SearchPath = _live.Name }.ConnectionString;
        _schemaDir = Path.Combine(Path.GetTempPath(), "schemorph-pg-online-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(Path.Combine(_schemaDir, "tables"));
    }

    public async Task DisposeAsync()
    {
        await _live.DisposeAsync();
        try { Directory.Delete(_schemaDir, recursive: true); } catch { }
    }

    private const string CheckConstraint = ",\n    CONSTRAINT \"CK_Doc_Qty\" CHECK (\"Qty\" >= 0)";

    /// <summary>The live table with a CHECK and a unique index added - one thing for each kind of online work.</summary>
    private Task DeclareCheckAndUniqueIndexAsync() => WriteDesiredAsync(
        constraints: CheckConstraint,
        indexes: $"""CREATE UNIQUE INDEX "IX_Doc_Name" ON "{_live.Name}"."Doc" ("Name");""");

    private Task WriteDesiredAsync(string constraints = "", string indexes = "")
        => File.WriteAllTextAsync(Path.Combine(_schemaDir, "tables", "schema.sql"), $"""
            CREATE TABLE "{_live.Name}"."Doc" (
                "Id" integer NOT NULL,
                "Name" text,
                "Qty" integer,
                CONSTRAINT "PK_Doc" PRIMARY KEY ("Id"){constraints}
            );
            {indexes}
            """);

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(PostgresTestSchema.ServerUrl);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<T> ScalarAsync<T>(string sql)
    {
        await using var connection = new NpgsqlConnection(PostgresTestSchema.ServerUrl);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    private string Doc => $"\"{_live.Name}\".\"Doc\"";

    /// <summary>Null when the index does not exist at all.</summary>
    private async Task<bool?> IndexIsValidAsync()
    {
        await using var connection = new NpgsqlConnection(PostgresTestSchema.ServerUrl);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT x.indisvalid FROM pg_index x JOIN pg_class i ON i.oid = x.indexrelid JOIN pg_namespace n ON n.oid = i.relnamespace " +
            "WHERE n.nspname = @s AND i.relname = 'IX_Doc_Name'", connection);
        command.Parameters.AddWithValue("s", _live.Name);
        return (bool?)await command.ExecuteScalarAsync();
    }

    /// <summary>Null when the constraint does not exist.</summary>
    private async Task<bool?> ConstraintIsValidatedAsync()
    {
        await using var connection = new NpgsqlConnection(PostgresTestSchema.ServerUrl);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            $"SELECT convalidated FROM pg_constraint WHERE conrelid = '{Doc}'::regclass AND conname = 'CK_Doc_Qty'", connection);
        return (bool?)await command.ExecuteScalarAsync();
    }

    private Task<ApplyOperation.Outcome> ApplyAsync(bool online = true, string? expectedPlanHash = null, TimeSpan? lockTimeout = null,
        CancellationToken cancellationToken = default)
        => ApplyOperation.RunAsync(_provider, _ledger,
            new ApplyOperation.Request(_schemaDir, _url, ExpectedPlanHash: expectedPlanHash, LockTimeout: lockTimeout, Online: online),
            cancellationToken: cancellationToken);

    private static string Describe(ApplyOperation.Outcome outcome)
        => string.Join("; ", outcome.Errors.Select(e => $"{e.Code}: {e.Text}"));

    // ---- the stage runs, and the plan says so

    [SkippableFact]
    public async Task The_index_is_built_and_the_constraint_validated_after_the_commit_and_the_apply_converges()
    {
        Skip.If(PostgresTestSchema.ServerUrl is null, "SCHEMORPH_PG_TEST_URL is not set; Postgres tests need a live server.");
        await DeclareCheckAndUniqueIndexAsync();

        var outcome = await ApplyAsync();

        Assert.True(outcome.Success, Describe(outcome));
        Assert.Equal(2, outcome.Plan!.Online.Count);
        Assert.StartsWith("CREATE UNIQUE INDEX CONCURRENTLY", outcome.Plan.Online[0].Sql);
        Assert.Contains("VALIDATE CONSTRAINT \"CK_Doc_Qty\"", outcome.Plan.Online[1].Sql);
        Assert.Equal(2, outcome.Online!.Completed.Count);
        Assert.Null(outcome.Online.Failed);
        Assert.True(await IndexIsValidAsync());
        Assert.True(await ConstraintIsValidatedAsync());

        // Nothing is left: the same apply plans nothing, online or not.
        var again = await ApplyAsync();
        Assert.True(again.Success, Describe(again));
        Assert.Empty(again.Applied);
        Assert.Empty(again.Plan!.Online);
        Assert.Null(again.Online);
    }

    [SkippableFact]
    public async Task Each_online_statement_is_recorded_in_the_history_ledger()
    {
        Skip.If(PostgresTestSchema.ServerUrl is null, "SCHEMORPH_PG_TEST_URL is not set; Postgres tests need a live server.");
        await DeclareCheckAndUniqueIndexAsync();

        var outcome = await ApplyAsync();
        Assert.True(outcome.Success, Describe(outcome));

        var rows = (await _ledger.ReadAsync(_url, "declarative")).Where(r => r.Operation == "Online").ToList();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.True(r.Succeeded));
        Assert.Contains(rows, r => r.Detail!.StartsWith("CREATE UNIQUE INDEX CONCURRENTLY"));
    }

    [SkippableFact]
    public async Task Without_the_option_the_same_change_is_built_inside_the_transaction_and_nothing_runs_after()
    {
        Skip.If(PostgresTestSchema.ServerUrl is null, "SCHEMORPH_PG_TEST_URL is not set; Postgres tests need a live server.");
        await DeclareCheckAndUniqueIndexAsync();

        var outcome = await ApplyAsync(online: false);

        Assert.True(outcome.Success, Describe(outcome));
        Assert.Empty(outcome.Plan!.Online);
        Assert.Null(outcome.Online);
        Assert.True(await IndexIsValidAsync());
        Assert.True(await ConstraintIsValidatedAsync());
        Assert.DoesNotContain(outcome.Plan.Actions, a => a.Sql!.Contains("CONCURRENTLY") || a.Sql.Contains("NOT VALID"));
    }

    [SkippableFact]
    public async Task The_index_lands_in_the_target_schema_not_the_default_one()
    {
        Skip.If(PostgresTestSchema.ServerUrl is null, "SCHEMORPH_PG_TEST_URL is not set; Postgres tests need a live server.");
        await DeclareCheckAndUniqueIndexAsync();

        Assert.True((await ApplyAsync()).Success);

        // The statement leaves the table unqualified; where it resolved is the session's search_path,
        // which a transaction-local setting could not have provided outside a transaction.
        Assert.Equal(1L, await ScalarAsync<long>(
            $"SELECT count(*) FROM pg_indexes WHERE schemaname = '{_live.Name}' AND indexname = 'IX_Doc_Name'"));
    }

    // ---- decision 6: a failure leaves something the next plan sees

    [SkippableFact]
    public async Task A_unique_build_over_duplicate_data_fails_the_stage_and_the_same_apply_converges_once_the_data_is_fixed()
    {
        Skip.If(PostgresTestSchema.ServerUrl is null, "SCHEMORPH_PG_TEST_URL is not set; Postgres tests need a live server.");
        await ExecuteAsync($"""INSERT INTO {Doc} ("Id", "Name", "Qty") VALUES (3, 'a', 3)""");   // 'a' twice
        await DeclareCheckAndUniqueIndexAsync();

        var failed = await ApplyAsync();

        Assert.False(failed.Success);
        Assert.Equal(ApplyOperation.FailureStage.Online, failed.Stage);
        Assert.Equal(ApplyOperation.Durability.Committed, failed.Durability);   // never "rolled back": it was not in the transaction
        Assert.Equal("online_execution_failed", Assert.Single(failed.Errors).Code);
        Assert.Equal("23505", failed.Engine!.Code);
        Assert.Empty(failed.Online!.Completed);
        Assert.NotNull(failed.Online.Failed);
        Assert.Equal(2, failed.Online.Total);
        Assert.Contains("de-duplicate", failed.Errors[0].Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("nothing was applied", failed.Errors[0].Text);

        // What it left: the failed build's invalid index, and the CHECK the transaction added NOT VALID.
        Assert.False(await IndexIsValidAsync());
        Assert.False(await ConstraintIsValidatedAsync());

        // The failure is in the ledger, and the stage-1 work is not undone.
        Assert.Contains(await _ledger.ReadAsync(_url, "declarative"), r => r.Operation == "Online" && !r.Succeeded);

        // Still duplicate: the same apply fails the same way (the invalid index is dropped and rebuilt).
        var stillFailing = await ApplyAsync();
        Assert.Equal(ApplyOperation.FailureStage.Online, stillFailing.Stage);

        await ExecuteAsync($"""DELETE FROM {Doc} WHERE "Id" = 3""");
        var converged = await ApplyAsync();

        Assert.True(converged.Success, Describe(converged));
        Assert.True(await IndexIsValidAsync());
        Assert.True(await ConstraintIsValidatedAsync());
        var again = await ApplyAsync();
        Assert.Empty(again.Plan!.Online);
    }

    [SkippableFact]
    public async Task A_validation_over_violating_rows_fails_the_stage_and_the_next_plan_is_the_validation_alone()
    {
        Skip.If(PostgresTestSchema.ServerUrl is null, "SCHEMORPH_PG_TEST_URL is not set; Postgres tests need a live server.");
        await ExecuteAsync($"""UPDATE {Doc} SET "Qty" = -5 WHERE "Id" = 2""");
        await WriteDesiredAsync(constraints: CheckConstraint);

        var failed = await ApplyAsync();

        Assert.False(failed.Success);
        Assert.Equal(ApplyOperation.FailureStage.Online, failed.Stage);
        Assert.Equal("23514", failed.Engine!.Code);
        // Added NOT VALID in the transaction: enforced for new rows, not yet validated for old ones.
        Assert.False(await ConstraintIsValidatedAsync());
        await Assert.ThrowsAsync<PostgresException>(() =>
            ExecuteAsync($"""INSERT INTO {Doc} ("Id", "Name", "Qty") VALUES (9, 'z', -1)"""));

        // Re-planning sees the leftover and plans only the validation - no second add, no drop.
        var plan = await DiffOperation.RunAsync(_provider, _ledger, _schemaDir, _url, allowDestructive: false, online: true);
        Assert.True(plan.Success);
        var step = Assert.Single(plan.Plan!.Online);
        Assert.Contains("VALIDATE CONSTRAINT \"CK_Doc_Qty\"", step.Sql);
        Assert.Null(plan.UpdateScript);   // nothing transactional is left to do

        await ExecuteAsync($"""UPDATE {Doc} SET "Qty" = 2 WHERE "Id" = 2""");
        var converged = await ApplyAsync();

        Assert.True(converged.Success, Describe(converged));
        Assert.True(await ConstraintIsValidatedAsync());
    }

    // ---- the gate

    [SkippableFact]
    public async Task The_plan_hash_binds_the_online_statements_so_a_diff_without_the_option_does_not_gate_an_apply_with_it()
    {
        Skip.If(PostgresTestSchema.ServerUrl is null, "SCHEMORPH_PG_TEST_URL is not set; Postgres tests need a live server.");
        await DeclareCheckAndUniqueIndexAsync();

        var plain = await DiffOperation.RunAsync(_provider, _ledger, _schemaDir, _url, allowDestructive: false);
        var online = await DiffOperation.RunAsync(_provider, _ledger, _schemaDir, _url, allowDestructive: false, online: true);
        Assert.True(plain.Success && online.Success);
        var plainHash = PlanHashOf(plain);
        var onlineHash = PlanHashOf(online);
        Assert.NotEqual(plainHash, onlineHash);

        var mismatch = await ApplyAsync(online: true, expectedPlanHash: plainHash);
        Assert.Equal(ApplyOperation.FailureStage.PlanMismatch, mismatch.Stage);
        var mismatchOther = await ApplyAsync(online: false, expectedPlanHash: onlineHash);
        Assert.Equal(ApplyOperation.FailureStage.PlanMismatch, mismatchOther.Stage);
        Assert.Null(await IndexIsValidAsync());   // neither ran anything

        var ok = await ApplyAsync(online: true, expectedPlanHash: onlineHash);
        Assert.True(ok.Success, Describe(ok));
    }

    private static string PlanHashOf(DiffOperation.DiffResult diff)
        => Schemorph.Core.Planning.PlanFingerprint.Compute(diff.Plan!);

    // ---- locks

    [SkippableFact]
    public async Task A_lock_another_session_holds_fails_the_online_statement_at_the_timeout_and_the_stage_is_reported()
    {
        Skip.If(PostgresTestSchema.ServerUrl is null, "SCHEMORPH_PG_TEST_URL is not set; Postgres tests need a live server.");
        await WriteDesiredAsync(indexes: $"""CREATE INDEX "IX_Doc_Name" ON "{_live.Name}"."Doc" ("Name");""");

        // SHARE ROW EXCLUSIVE conflicts with the SHARE UPDATE EXCLUSIVE a concurrent build takes, but not
        // with the reads the planning does - so the apply reaches the online stage and waits there.
        await using var holder = new NpgsqlConnection(PostgresTestSchema.ServerUrl);
        await holder.OpenAsync();
        await using var holderTransaction = await holder.BeginTransactionAsync();
        await using (var lockCommand = new NpgsqlCommand($"LOCK TABLE {Doc} IN SHARE ROW EXCLUSIVE MODE", holder, holderTransaction))
        {
            await lockCommand.ExecuteNonQueryAsync();
        }

        using var ceiling = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var failed = await ApplyAsync(lockTimeout: TimeSpan.FromMilliseconds(600), cancellationToken: ceiling.Token);

        Assert.False(failed.Success);
        Assert.Equal(ApplyOperation.FailureStage.Online, failed.Stage);
        Assert.Equal("55P03", failed.Engine!.Code);
        Assert.Contains("online stage", failed.Errors[0].Text);
        Assert.DoesNotContain("nothing was applied", failed.Errors[0].Text);   // the ordinary hint would be false here
        Assert.Empty(failed.Online!.Completed);

        await holderTransaction.RollbackAsync();
        var rerun = await ApplyAsync();
        Assert.True(rerun.Success, Describe(rerun));
        Assert.True(await IndexIsValidAsync());
    }

    [SkippableFact]
    public async Task The_session_settings_of_an_online_statement_do_not_outlive_it()
    {
        Skip.If(PostgresTestSchema.ServerUrl is null, "SCHEMORPH_PG_TEST_URL is not set; Postgres tests need a live server.");

        await _provider.ExecuteOnlineStepAsync(_url,
            new OnlineStep("Doc", $"""CREATE INDEX CONCURRENTLY "IX_Doc_Name" ON "Doc" ("Name");"""),
            TimeSpan.FromSeconds(7));

        // The next user of the pooled connection must see the server's setting, not this statement's.
        await using var next = new NpgsqlConnection(_url);
        await next.OpenAsync();
        await using var show = new NpgsqlCommand("SHOW lock_timeout", next);
        Assert.Equal("0", (string)(await show.ExecuteScalarAsync())!);
        Assert.True(await IndexIsValidAsync());
    }

    [SkippableFact]
    public async Task A_statement_the_server_rejects_is_described_for_the_stage_it_ran_in()
    {
        Skip.If(PostgresTestSchema.ServerUrl is null, "SCHEMORPH_PG_TEST_URL is not set; Postgres tests need a live server.");

        var ex = await Assert.ThrowsAsync<PostgresOnlineStepException>(() => _provider.ExecuteOnlineStepAsync(_url,
            new OnlineStep("Doc", """CREATE INDEX CONCURRENTLY "IX_x" ON "NoSuchTable" ("Name");""")));

        var engine = _provider.DescribeEngineError(ex)!;
        Assert.Equal("42P01", engine.Code);
        Assert.Equal(PostgresSqlStateHints.TryGet("42P01"), engine.Hint);   // no online-specific text for this code
    }
}
