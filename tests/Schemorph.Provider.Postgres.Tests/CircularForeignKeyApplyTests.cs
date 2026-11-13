using Npgsql;
using Schemorph.Core.Operations;

namespace Schemorph.Provider.Postgres.Tests;

/// <summary>
/// Two tables that point at each other (a department and its head, an employee and a department) used to stop the whole
/// comparison: the shadow could not create either table first. The key that closes the cycle is now added after both
/// tables exist, so the desired state is applied as written, and the next run sees nothing to change.
/// </summary>
public sealed class CircularForeignKeyApplyTests : IAsyncDisposable
{
    private readonly string _schemaName = "schemorph_cyc_" + Guid.NewGuid().ToString("n")[..12];
    private readonly string _schemaDir = Path.Combine(
        Path.GetTempPath(), "schemorph-pg-cyc-" + Guid.NewGuid().ToString("n")[..8]);

    public async ValueTask DisposeAsync()
    {
        if (PostgresTestSchema.ServerUrl is not null)
        {
            await using var connection = new NpgsqlConnection(PostgresTestSchema.ServerUrl);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"DROP SCHEMA IF EXISTS \"{_schemaName}\" CASCADE", connection);
            await command.ExecuteNonQueryAsync();
        }
        try { Directory.Delete(_schemaDir, recursive: true); } catch { }
    }

    private async Task WriteAsync(string name, string sql)
    {
        Directory.CreateDirectory(Path.Combine(_schemaDir, "tables"));
        await File.WriteAllTextAsync(Path.Combine(_schemaDir, "tables", name + ".sql"), sql);
    }

    private async Task<ApplyOperation.Outcome> ApplyAsync()
    {
        var url = new NpgsqlConnectionStringBuilder(PostgresTestSchema.ServerUrl!) { SearchPath = _schemaName }.ConnectionString;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        return await ApplyOperation.RunAsync(
            new PostgresProvider(), new PostgresLedgerStore(), new ApplyOperation.Request(_schemaDir, url), cancellationToken: cts.Token);
    }

    private async Task<List<string>> ForeignKeysAsync()
    {
        await using var connection = new NpgsqlConnection(PostgresTestSchema.ServerUrl);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT c.conrelid::regclass::text || ' -> ' || c.confrelid::regclass::text || ' (' || c.conname || ')'
            FROM pg_constraint c JOIN pg_namespace n ON n.oid = c.connamespace
            WHERE c.contype = 'f' AND n.nspname = @schema
            ORDER BY 1
            """, connection);
        command.Parameters.AddWithValue("schema", _schemaName);
        var found = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) found.Add(reader.GetString(0));
        return found;
    }

    [SkippableFact]
    public async Task A_desired_state_whose_tables_reference_each_other_applies_and_then_converges()
    {
        Skip.If(PostgresTestSchema.ServerUrl is null, "SCHEMORPH_PG_TEST_URL is not set; Postgres tests need a live server.");

        await WriteAsync("Dept", $"""
            CREATE TABLE "{_schemaName}"."Dept" (
                "Id" uuid NOT NULL,
                "HeadId" uuid REFERENCES "{_schemaName}"."Emp" ("Id"),
                CONSTRAINT "PK_Dept" PRIMARY KEY ("Id")
            );
            """);
        await WriteAsync("Emp", $"""
            CREATE TABLE "{_schemaName}"."Emp" (
                "Id" uuid NOT NULL,
                "DeptId" uuid,
                CONSTRAINT "PK_Emp" PRIMARY KEY ("Id"),
                CONSTRAINT "FK_Emp_Dept" FOREIGN KEY ("DeptId") REFERENCES "{_schemaName}"."Dept" ("Id")
            );
            """);

        var first = await ApplyAsync();

        Assert.True(first.Success, string.Join("; ", first.Errors.Select(e => e.Text)));
        var keys = await ForeignKeysAsync();
        Assert.Equal(2, keys.Count);                       // both keys exist in the live schema, as the user wrote them
        Assert.Contains(keys, k => k.EndsWith("(FK_Emp_Dept)", StringComparison.Ordinal));   // a named key kept its name
        Assert.Contains(keys, k => k.Contains("(Dept_HeadId_fkey)", StringComparison.Ordinal)); // an unnamed one got the engine's own

        // Nothing declared differs from what is live, so a second run has nothing to do — the keys are not drift.
        var second = await ApplyAsync();
        Assert.True(second.Success, string.Join("; ", second.Errors.Select(e => e.Text)));
        Assert.Empty(second.Applied);
        Assert.Equal(2, (await ForeignKeysAsync()).Count);
    }

    /// <summary>
    /// The way around the old refusal was to write the keys as separate <c>ALTER TABLE</c> statements. Declared in the desired
    /// state they are part of it — kept, and not drift on the next run (a key added by hand outside the desired state is
    /// another matter: that one is undeclared).
    /// </summary>
    [SkippableFact]
    public async Task Keys_declared_as_separate_alter_statements_are_kept_and_converge()
    {
        Skip.If(PostgresTestSchema.ServerUrl is null, "SCHEMORPH_PG_TEST_URL is not set; Postgres tests need a live server.");

        await WriteAsync("Dept", $"""
            CREATE TABLE "{_schemaName}"."Dept" ("Id" uuid NOT NULL, "HeadId" uuid, CONSTRAINT "PK_Dept" PRIMARY KEY ("Id"));
            ALTER TABLE "{_schemaName}"."Dept" ADD CONSTRAINT "FK_Dept_Emp" FOREIGN KEY ("HeadId") REFERENCES "{_schemaName}"."Emp" ("Id");
            """);
        await WriteAsync("Emp", $"""
            CREATE TABLE "{_schemaName}"."Emp" ("Id" uuid NOT NULL, "DeptId" uuid, CONSTRAINT "PK_Emp" PRIMARY KEY ("Id"));
            ALTER TABLE "{_schemaName}"."Emp" ADD CONSTRAINT "FK_Emp_Dept" FOREIGN KEY ("DeptId") REFERENCES "{_schemaName}"."Dept" ("Id");
            """);

        var first = await ApplyAsync();
        Assert.True(first.Success, string.Join("; ", first.Errors.Select(e => e.Text)));
        Assert.Equal(2, (await ForeignKeysAsync()).Count);

        var second = await ApplyAsync();
        Assert.True(second.Success, string.Join("; ", second.Errors.Select(e => e.Text)));
        Assert.Empty(second.Applied);
        Assert.Equal(2, (await ForeignKeysAsync()).Count);
    }
}
