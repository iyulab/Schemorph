using Npgsql;
using Schemorph.Core.Operations;

namespace Schemorph.Provider.Postgres.Tests;

/// <summary>
/// A statement that fails part-way through a concurrent index build leaves an <em>invalid</em> index
/// behind; a constraint added <c>NOT VALID</c> stays unvalidated until someone validates it. Both
/// are real states of a live database, produced here the way a real one gets them (a build that
/// really fails on duplicate data, a constraint really added <c>NOT VALID</c>), and the property
/// under test is the one every apply failure leans on: running the same apply again converges.
/// Before the inspector read these states the first was reported as in sync, forever.
/// </summary>
public sealed class PgInvalidStateConvergenceTests : IAsyncLifetime
{
    private PgTestSchema _live = null!;
    private string _url = null!;
    private string _schemaDir = null!;
    private readonly PostgresProvider _provider = new();
    private readonly PostgresLedgerStore _ledger = new();

    public async Task InitializeAsync()
    {
        _live = await PgTestSchema.CreateAsync("""
            CREATE TABLE "Doc" (
                "Id" integer NOT NULL,
                "Name" text,
                "Qty" integer,
                CONSTRAINT "PK_Doc" PRIMARY KEY ("Id")
            );
            """);
        _url = new NpgsqlConnectionStringBuilder(PgTestSchema.ServerUrl!) { SearchPath = _live.Name }.ConnectionString;
        _schemaDir = Path.Combine(Path.GetTempPath(), "schemorph-pg-invalid-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(Path.Combine(_schemaDir, "tables"));
        await File.WriteAllTextAsync(Path.Combine(_schemaDir, "tables", "schema.sql"), $"""
            CREATE TABLE "{_live.Name}"."Doc" (
                "Id" integer NOT NULL,
                "Name" text,
                "Qty" integer,
                CONSTRAINT "PK_Doc" PRIMARY KEY ("Id"),
                CONSTRAINT "CK_Doc_Qty" CHECK ("Qty" >= 0)
            );
            CREATE UNIQUE INDEX "IX_Doc_Name" ON "{_live.Name}"."Doc" ("Name");
            """);
    }

    public async Task DisposeAsync()
    {
        await _live.DisposeAsync();
        try { Directory.Delete(_schemaDir, recursive: true); } catch { }
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(PgTestSchema.ServerUrl);
        await connection.OpenAsync();
        // One statement per command, schema-qualified: a multi-statement text runs in an implicit
        // transaction, and CREATE INDEX CONCURRENTLY refuses to.
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private string Doc => $"\"{_live.Name}\".\"Doc\"";

    private async Task<T> ScalarAsync<T>(string sql)
    {
        await using var connection = new NpgsqlConnection(PgTestSchema.ServerUrl);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    private Task<bool> IndexIsValidAsync() => ScalarAsync<bool>(
        $"SELECT x.indisvalid FROM pg_index x JOIN pg_class i ON i.oid = x.indexrelid JOIN pg_namespace n ON n.oid = i.relnamespace " +
        $"WHERE n.nspname = '{_live.Name}' AND i.relname = 'IX_Doc_Name'");

    private Task<bool> ConstraintIsValidatedAsync() => ScalarAsync<bool>(
        $"SELECT convalidated FROM pg_constraint WHERE conrelid = '\"{_live.Name}\".\"Doc\"'::regclass AND conname = 'CK_Doc_Qty'");

    private Task<ApplyOperation.Outcome> ApplyAsync()
        => ApplyOperation.RunAsync(_provider, _ledger, new ApplyOperation.Request(_schemaDir, _url));

    private static string Describe(ApplyOperation.Outcome outcome)
        => string.Join("; ", outcome.Errors.Select(e => $"{e.Code}: {e.Text}"));

    [SkippableFact]
    public async Task An_index_a_failed_concurrent_build_left_invalid_is_rebuilt_not_reported_in_sync()
    {
        Skip.If(PgTestSchema.ServerUrl is null, "SCHEMORPH_PG_TEST_URL is not set; Postgres tests need a live server.");

        // The way a real invalid index is made: a unique build that really fails on duplicate data.
        await ExecuteAsync($"""INSERT INTO {Doc} ("Id", "Name", "Qty") VALUES (1, 'dup', 1), (2, 'dup', 1)""");
        var failure = await Assert.ThrowsAsync<PostgresException>(() =>
            ExecuteAsync($"""CREATE UNIQUE INDEX CONCURRENTLY "IX_Doc_Name" ON {Doc} ("Name")"""));
        Assert.Equal("23505", failure.SqlState);
        Assert.False(await IndexIsValidAsync());           // there, and useless

        // Its definition reads exactly like the declared index, so a comparison of text alone would
        // call the schema in sync. The apply must instead try to rebuild it: with the duplicates
        // still there the rebuild fails the same way, and nothing changes (one transaction).
        var blocked = await ApplyAsync();
        Assert.False(blocked.Success);
        Assert.Equal("23505", Assert.Single(blocked.Errors).Code);
        Assert.False(await IndexIsValidAsync());

        // Fix the data and run the same apply again: it converges, the index is whole.
        await ExecuteAsync($"""DELETE FROM {Doc} WHERE "Id" = 2""");
        var rebuilt = await ApplyAsync();
        Assert.True(rebuilt.Success, Describe(rebuilt));
        Assert.True(await IndexIsValidAsync());

        // And that was the whole difference: nothing is left to do.
        var again = await ApplyAsync();
        Assert.True(again.Success, Describe(again));
        Assert.Empty(again.Applied);
    }

    [SkippableFact]
    public async Task A_constraint_added_not_valid_is_validated_in_place_and_converges()
    {
        Skip.If(PgTestSchema.ServerUrl is null, "SCHEMORPH_PG_TEST_URL is not set; Postgres tests need a live server.");

        // A row that predates the constraint and breaks it: ADD ... NOT VALID accepts that.
        await ExecuteAsync($"""INSERT INTO {Doc} ("Id", "Name", "Qty") VALUES (1, 'a', 5), (2, 'b', -1)""");
        await ExecuteAsync($"""ALTER TABLE {Doc} ADD CONSTRAINT "CK_Doc_Qty" CHECK ("Qty" >= 0) NOT VALID""");
        await ExecuteAsync($"""CREATE UNIQUE INDEX "IX_Doc_Name" ON {Doc} ("Name")""");
        Assert.False(await ConstraintIsValidatedAsync());
        var oidBefore = await ScalarAsync<uint>(
            $"SELECT oid FROM pg_constraint WHERE conrelid = '\"{_live.Name}\".\"Doc\"'::regclass AND conname = 'CK_Doc_Qty'");

        // The declared constraint is the validated one, so the apply has to validate it — and it
        // cannot while the offending row is there. The failure is the engine's, and nothing changes.
        var blocked = await ApplyAsync();
        Assert.False(blocked.Success);
        Assert.Equal("23514", Assert.Single(blocked.Errors).Code);
        Assert.False(await ConstraintIsValidatedAsync());

        // Fix the row and apply again: validated.
        await ExecuteAsync($"""UPDATE {Doc} SET "Qty" = 0 WHERE "Id" = 2""");
        var validated = await ApplyAsync();
        Assert.True(validated.Success, Describe(validated));
        Assert.True(await ConstraintIsValidatedAsync());

        // The same constraint, validated where it stood — not dropped and added again, which would
        // have taken the stronger lock and given it a new identity.
        var oidAfter = await ScalarAsync<uint>(
            $"SELECT oid FROM pg_constraint WHERE conrelid = '\"{_live.Name}\".\"Doc\"'::regclass AND conname = 'CK_Doc_Qty'");
        Assert.Equal(oidBefore, oidAfter);

        var again = await ApplyAsync();
        Assert.True(again.Success, Describe(again));
        Assert.Empty(again.Applied);
    }
}
