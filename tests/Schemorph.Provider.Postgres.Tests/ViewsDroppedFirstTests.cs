using Npgsql;
using Schemorph.Core.Operations;
using Schemorph.Core.Planning;

namespace Schemorph.Provider.Postgres.Tests;

/// <summary>
/// A view that reads a column blocks that column's drop and its retype (SQLSTATE 2BP01 /
/// 0A000) for as long as it exists, and re-definition only runs after the declarative stage.
/// So an approved plan that changes the view and drops or retypes the column it used to read
/// failed at apply time, the one place the plan cannot warn about. The declarative script now
/// drops such a view first and the redefine stage re-creates it from its file. Each test
/// starts from a state the tool itself applied, so the views carry history — an unchanged
/// file really is unchanged to the ledger.
/// </summary>
public sealed class ViewsDroppedFirstTests : IAsyncLifetime
{
    private PgTestSchema _live = null!;
    private string _url = null!;
    private string _dir = null!;
    private readonly PostgresProvider _provider = new();
    private readonly PostgresLedgerStore _ledger = new();

    public async Task InitializeAsync()
    {
        _live = await PgTestSchema.CreateAsync("SELECT 1;");
        _url = new NpgsqlConnectionStringBuilder(PgTestSchema.ServerUrl!) { SearchPath = _live.Name }
            .ConnectionString;
        _dir = Path.Combine(Path.GetTempPath(), "schemorph-pg-dropfirst-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(Path.Combine(_dir, "tables"));
        Directory.CreateDirectory(Path.Combine(_dir, "views"));
    }

    public async Task DisposeAsync()
    {
        await _live.DisposeAsync();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private async Task WriteAsync(string table, params (string Name, string Sql)[] views)
    {
        await File.WriteAllTextAsync(Path.Combine(_dir, "tables", "orders.sql"), table);
        foreach (var file in Directory.GetFiles(Path.Combine(_dir, "views"))) File.Delete(file);
        foreach (var (name, sql) in views)
        {
            await File.WriteAllTextAsync(Path.Combine(_dir, "views", name + ".sql"), sql);
        }
    }

    private async Task ApplyAsync(bool allowDestructive = false)
    {
        var outcome = await ApplyOperation.RunAsync(
            _provider, _ledger, new ApplyOperation.Request(_dir, _url, AllowDestructive: allowDestructive));
        Assert.True(outcome.Success, string.Join("; ", outcome.Errors.Select(e => $"{e.Code}: {e.Text}")));
    }

    private async Task<Plan> DiffAsync(bool allowDestructive = false)
    {
        var diff = await DiffOperation.RunAsync(_provider, _ledger, _dir, _url, allowDestructive);
        Assert.True(diff.Success, string.Join("; ", diff.Errors.Select(e => $"{e.Code}: {e.Text}")));
        return diff.Plan!;
    }

    private async Task<T?> ScalarAsync<T>(string sql)
    {
        await using var connection = new NpgsqlConnection(_url);
        await connection.OpenAsync();
        return (T?)await new NpgsqlCommand(sql, connection).ExecuteScalarAsync();
    }

    private async Task SeedAsync()
    {
        await WriteAsync("CREATE TABLE orders (id int PRIMARY KEY, a int NOT NULL, b int NOT NULL);",
            ("order_b", "CREATE VIEW order_b AS SELECT id, b FROM orders;"));
        await ApplyAsync();
        await PgTestSchema.ExecuteAsync($"""INSERT INTO "{_live.Name}".orders VALUES (1, 2, 3);""");
    }

    [SkippableFact]
    public async Task Changing_a_view_and_dropping_the_column_it_read_applies_in_one_run()
    {
        await SeedAsync();
        await WriteAsync("CREATE TABLE orders (id int PRIMARY KEY, a int NOT NULL);",
            ("order_b", "CREATE VIEW order_b AS SELECT id, a FROM orders;"));

        var plan = await DiffAsync(allowDestructive: true);
        Assert.Contains($"DROP VIEW IF EXISTS \"{_live.Name}\".\"order_b\";", plan.UpdateScript);
        var view = Assert.Single(plan.Actions, a => a.ObjectName == "order_b");
        Assert.Equal(PlanOperation.Redefine, view.Operation);
        Assert.Contains("declarative script drops it first", view.Explanation);

        await ApplyAsync(allowDestructive: true);

        Assert.False((await DiffAsync()).HasChanges);
        Assert.Equal(2, await ScalarAsync<int>("SELECT a FROM order_b WHERE id = 1"));
    }

    [SkippableFact]
    public async Task Retyping_a_column_an_unchanged_view_reads_re_creates_the_view()
    {
        await SeedAsync();
        // The view's file does not change: to the ledger it is up to date, and only the
        // column under it moves.
        await WriteAsync("CREATE TABLE orders (id int PRIMARY KEY, a int NOT NULL, b bigint NOT NULL);",
            ("order_b", "CREATE VIEW order_b AS SELECT id, b FROM orders;"));

        await ApplyAsync(allowDestructive: true);

        Assert.False((await DiffAsync()).HasChanges);
        Assert.Equal("bigint", await ScalarAsync<string>(
            $"SELECT data_type FROM information_schema.columns WHERE table_schema = '{_live.Name}' " +
            "AND table_name = 'order_b' AND column_name = 'b'"));
    }

    [SkippableFact]
    public async Task A_view_over_the_dropped_view_goes_first_too_and_comes_back()
    {
        await WriteAsync("CREATE TABLE orders (id int PRIMARY KEY, a int NOT NULL, b int NOT NULL);",
            ("order_x", "CREATE VIEW order_x AS SELECT id, b AS x FROM orders;"),
            ("order_x_ids", "CREATE VIEW order_x_ids AS SELECT id, x FROM order_x;"));
        await ApplyAsync();
        await PgTestSchema.ExecuteAsync($"""INSERT INTO "{_live.Name}".orders VALUES (1, 2, 3);""");

        // The inner view keeps its shape (a plain CREATE OR REPLACE would do), but it reads
        // the column being dropped; the outer view's file is untouched and reads the inner.
        await WriteAsync("CREATE TABLE orders (id int PRIMARY KEY, a int NOT NULL);",
            ("order_x", "CREATE VIEW order_x AS SELECT id, a AS x FROM orders;"),
            ("order_x_ids", "CREATE VIEW order_x_ids AS SELECT id, x FROM order_x;"));

        var plan = await DiffAsync(allowDestructive: true);
        var script = plan.UpdateScript!;
        Assert.True(script.IndexOf("\"order_x_ids\"", StringComparison.Ordinal)
                    < script.IndexOf("\"order_x\";", StringComparison.Ordinal),
            "the view reading another must be dropped before the one it reads");
        Assert.Contains(plan.Actions, a => a.ObjectName == "order_x_ids" && a.Operation == PlanOperation.Redefine);

        await ApplyAsync(allowDestructive: true);

        Assert.False((await DiffAsync()).HasChanges);
        Assert.Equal(2, await ScalarAsync<int>("SELECT x FROM order_x_ids WHERE id = 1"));
    }

    [SkippableFact]
    public async Task A_view_over_a_view_can_be_compared_again_once_applied()
    {
        // The comparison probes each live view's file against the desired tables in a
        // shadow schema; a view reading another view could not be probed there, so once
        // such a pair was applied, every later diff failed (42P01) with nothing changed.
        await WriteAsync("CREATE TABLE orders (id int PRIMARY KEY, a int NOT NULL);",
            ("order_a", "CREATE VIEW order_a AS SELECT id, a FROM orders;"),
            ("order_a_ids", "CREATE VIEW order_a_ids AS SELECT id FROM order_a;"));
        await ApplyAsync();

        Assert.False((await DiffAsync()).HasChanges);
    }

    [SkippableFact]
    public async Task A_view_calling_a_declared_function_can_be_compared_again_once_applied()
    {
        // Same probe, same gap: the shadow had no functions either, so a view calling one the
        // desired state declares failed every later diff with 42883.
        Directory.CreateDirectory(Path.Combine(_dir, "functions"));
        await File.WriteAllTextAsync(Path.Combine(_dir, "functions", "double_it.sql"),
            "CREATE FUNCTION double_it(v int) RETURNS int LANGUAGE sql IMMUTABLE AS $$ SELECT v * 2 $$;");
        await WriteAsync("CREATE TABLE orders (id int PRIMARY KEY, a int NOT NULL);",
            ("order_doubled", "CREATE VIEW order_doubled AS SELECT id, double_it(a) AS a2 FROM orders;"));
        await ApplyAsync();

        Assert.False((await DiffAsync()).HasChanges);
    }

    [SkippableFact]
    public async Task A_view_reading_only_columns_that_stay_is_left_alone()
    {
        await WriteAsync("CREATE TABLE orders (id int PRIMARY KEY, a int NOT NULL, b int NOT NULL);",
            ("order_a", "CREATE VIEW order_a AS SELECT id, a FROM orders;"));
        await ApplyAsync();

        await WriteAsync("CREATE TABLE orders (id int PRIMARY KEY, a int NOT NULL);",
            ("order_a", "CREATE VIEW order_a AS SELECT id, a FROM orders;"));

        var plan = await DiffAsync(allowDestructive: true);
        Assert.DoesNotContain("order_a", plan.UpdateScript);
        Assert.DoesNotContain(plan.Actions, a => a.ObjectName == "order_a");
    }
}
