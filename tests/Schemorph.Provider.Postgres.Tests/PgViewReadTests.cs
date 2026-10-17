using Npgsql;
using Schemorph.Core.Operations;

namespace Schemorph.Provider.Postgres.Tests;

/// <summary>
/// A view definition can compile and still fail on the rows already there — a cast the values overflow, a division by
/// zero. Only reading it shows that, and without a read the first to find out is every query that touches the view after
/// the apply reported success. The re-definition reads the view once before the apply's transaction commits
/// (<see cref="PostgresProvider.RedefineAsync"/>), so the apply fails at the view and rolls back — the previous
/// definition stays.
/// </summary>
public class PgViewReadTests : IAsyncLifetime
{
    private PgTestSchema _live = null!;
    private string _url = null!;
    private string _schemaDir = null!;
    private readonly PostgresProvider _provider = new();
    private readonly PostgresLedgerStore _ledger = new();

    public async Task InitializeAsync()
    {
        _live = await PgTestSchema.CreateAsync("");
        _url = new NpgsqlConnectionStringBuilder(PgTestSchema.ServerUrl!) { SearchPath = _live.Name }
            .ConnectionString;
        _schemaDir = Path.Combine(Path.GetTempPath(), "schemorph-pg-viewread-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(Path.Combine(_schemaDir, "tables"));
        Directory.CreateDirectory(Path.Combine(_schemaDir, "views"));
        await File.WriteAllTextAsync(Path.Combine(_schemaDir, "tables", "lines.sql"), $"""
            CREATE TABLE "{_live.Name}".lines (
                id integer NOT NULL,
                price numeric(18, 2) NOT NULL,
                list_price numeric(18, 2) NOT NULL,
                CONSTRAINT pk_lines PRIMARY KEY (id)
            );
            """);
    }

    public async Task DisposeAsync()
    {
        await _live.DisposeAsync();
        try { Directory.Delete(_schemaDir, recursive: true); } catch { }
    }

    private Task WriteView(string ratio) => File.WriteAllTextAsync(
        Path.Combine(_schemaDir, "views", "line_ratios.sql"),
        $"CREATE VIEW line_ratios AS SELECT id, {ratio} AS ratio FROM lines;");

    private async Task<ApplyOperation.Outcome> ApplyAsync() =>
        await ApplyOperation.RunAsync(_provider, _ledger, new ApplyOperation.Request(_schemaDir, _url));

    private async Task<object?> ScalarAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(_url);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteScalarAsync();
    }

    private async Task ExecuteAsync(string sql) => await ScalarAsync(sql);

    [SkippableFact]
    public async Task A_new_view_definition_the_rows_overflow_fails_its_apply_and_keeps_the_previous_one()
    {
        await WriteView("CAST(price * 100 / list_price AS numeric(12, 1))");
        var created = await ApplyAsync();
        Assert.True(created.Success, string.Join("; ", created.Errors.Select(e => e.Text)));
        await ExecuteAsync("INSERT INTO lines VALUES (1, 12000, 1), (2, 50, 100)");

        // Narrower than the first row's 1,200,000.0 — the definition itself is valid.
        await WriteView("CAST(price * 100 / list_price AS numeric(5, 1))");
        var outcome = await ApplyAsync();

        Assert.False(outcome.Success);
        Assert.Equal(ApplyOperation.FailureStage.Redefine, outcome.Stage);
        Assert.Equal(ApplyOperation.Durability.RolledBack, outcome.Durability);
        var error = string.Join("; ", outcome.Errors.Select(e => e.Text));
        Assert.Contains("line_ratios", error);
        Assert.Contains("22003", error);

        // The previous definition is in place and still reads.
        Assert.Contains("12,1", ((string)(await ScalarAsync("SELECT pg_get_viewdef('line_ratios'::regclass)"))!)
            .Replace(" ", ""));
        Assert.Equal(2L, await ScalarAsync("SELECT count(*) FROM line_ratios"));
    }

    [SkippableFact]
    public async Task A_new_view_definition_that_divides_a_row_by_zero_fails_its_apply()
    {
        await WriteView("price");
        Assert.True((await ApplyAsync()).Success);
        await ExecuteAsync("INSERT INTO lines VALUES (1, 10, 0)");

        await WriteView("price / list_price");
        var outcome = await ApplyAsync();

        Assert.False(outcome.Success);
        Assert.Contains("22012", string.Join("; ", outcome.Errors.Select(e => e.Text)));
    }

    [SkippableFact]
    public async Task A_narrower_definition_the_rows_fit_is_applied()
    {
        await WriteView("CAST(price * 100 / list_price AS numeric(12, 1))");
        Assert.True((await ApplyAsync()).Success);
        await ExecuteAsync("INSERT INTO lines VALUES (1, 50, 100)");

        await WriteView("CAST(price * 100 / list_price AS numeric(5, 1))");
        var outcome = await ApplyAsync();

        Assert.True(outcome.Success, string.Join("; ", outcome.Errors.Select(e => e.Text)));
        Assert.Contains("5,1", ((string)(await ScalarAsync("SELECT pg_get_viewdef('line_ratios'::regclass)"))!)
            .Replace(" ", ""));
    }
}
