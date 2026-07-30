using Npgsql;
using Schemorph.Core.Operations;

namespace Schemorph.Provider.Postgres.Tests;

/// <summary>
/// Adding a column to an existing table is the most ordinary declarative change
/// there is, and it is the one shape where the desired state and the database
/// can never agree on column order: PostgreSQL appends the new column, while a
/// model-generated file lists it wherever it belongs logically — typically ahead
/// of trailing audit columns, which no ALTER can move it past. If the comparison
/// treats ordinal position as state, that difference is planned as an alter no
/// statement can carry out, so the plan never empties and every convergence gate
/// downstream stays red on a database that is in fact correct.
///
/// The policy this pins is not new — it is the project-wide "diff state, not
/// ordinal position" that the SQL Server provider states as `IgnoreColumnOrder`.
/// This test holds the second provider to it end to end, through the same core
/// operations every surface renders.
/// </summary>
public class ColumnOrderConvergenceTests : IAsyncLifetime
{
    private PgTestSchema _live = null!;
    private string _url = null!;
    private string _schemaDir = null!;
    private readonly PostgresProvider _provider = new();
    private readonly PostgresLedgerStore _ledger = new();

    // The live table before the change: no "state" column.
    private const string LiveV1 = """
        CREATE TABLE task_record (
            id uuid NOT NULL,
            title text NOT NULL,
            created_at timestamptz NOT NULL DEFAULT now(),
            updated_at timestamptz NOT NULL DEFAULT now(),
            CONSTRAINT pk_task_record PRIMARY KEY (id)
        );
        """;

    public async Task InitializeAsync()
    {
        _live = await PgTestSchema.CreateAsync(LiveV1);
        _url = new NpgsqlConnectionStringBuilder(PgTestSchema.ServerUrl!) { SearchPath = _live.Name }
            .ConnectionString;

        // The desired state declares "state" in the middle — before the audit
        // columns a generator always emits last. Postgres will append it, so the
        // two sides can only ever agree if order is excluded from the comparison.
        _schemaDir = Path.Combine(
            Path.GetTempPath(), "schemorph-pg-order-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(Path.Combine(_schemaDir, "tables"));
        await File.WriteAllTextAsync(Path.Combine(_schemaDir, "tables", "task_record.sql"), $"""
            CREATE TABLE "{_live.Name}".task_record (
                id uuid NOT NULL,
                title text NOT NULL,
                state varchar(20) NOT NULL DEFAULT 'open',
                created_at timestamptz NOT NULL DEFAULT now(),
                updated_at timestamptz NOT NULL DEFAULT now(),
                CONSTRAINT pk_task_record PRIMARY KEY (id),
                CONSTRAINT ck_task_record_state CHECK (state IN ('open', 'closed'))
            );
            """);
    }

    public async Task DisposeAsync()
    {
        await _live.DisposeAsync();
        try { Directory.Delete(_schemaDir, recursive: true); } catch { }
    }

    [SkippableFact]
    public async Task A_column_added_mid_table_converges_after_apply()
    {
        var diff = await DiffOperation.RunAsync(_provider, _ledger, _schemaDir, _url, allowDestructive: false);
        Assert.True(diff.Success, string.Join("; ", diff.Errors.Select(e => e.Text)));
        Assert.True(diff.Plan!.HasChanges);

        var apply = await ApplyOperation.RunAsync(_provider, _ledger,
            new ApplyOperation.Request(_schemaDir, _url, ExpectedPlanHash: null));
        Assert.True(apply.Success, string.Join("; ", apply.Errors.Select(e => e.Text)));

        // The column exists, and it exists LAST — the physical order the engine
        // chose, not the declared one. That is the state this test is about.
        Assert.Equal(
            ["id", "title", "created_at", "updated_at", "state"],
            await ColumnNamesAsync());

        var rediff = await DiffOperation.RunAsync(_provider, _ledger, _schemaDir, _url, allowDestructive: false);
        Assert.True(rediff.Success, string.Join("; ", rediff.Errors.Select(e => e.Text)));
        Assert.False(rediff.Plan!.HasChanges,
            "a column the engine appended must not re-plan as a perpetual alter");

        // Stability: a second apply is a no-op and the loop stays empty.
        var applyAgain = await ApplyOperation.RunAsync(_provider, _ledger,
            new ApplyOperation.Request(_schemaDir, _url, ExpectedPlanHash: null));
        Assert.True(applyAgain.Success);
        Assert.Empty(applyAgain.Applied);

        var rediffAgain = await DiffOperation.RunAsync(_provider, _ledger, _schemaDir, _url, allowDestructive: false);
        Assert.False(rediffAgain.Plan!.HasChanges);
    }

    private async Task<List<string>> ColumnNamesAsync()
    {
        await using var connection = new NpgsqlConnection(_url);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT a.attname
            FROM pg_attribute a
            JOIN pg_class c ON c.oid = a.attrelid
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = @schema AND c.relname = 'task_record'
              AND a.attnum > 0 AND NOT a.attisdropped
            ORDER BY a.attnum;
            """, connection);
        command.Parameters.AddWithValue("schema", _live.Name);

        var names = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) names.Add(reader.GetString(0));
        return names;
    }
}
