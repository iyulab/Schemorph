using Npgsql;
using Schemorph.Core.Operations;

namespace Schemorph.Provider.Postgres.Tests;

using ProgrammableFile = PgDesiredState.ProgrammableFile;

/// <summary>
/// The column probe builds each changed view in a scratch schema holding the desired state. A
/// function an extension installed in the target schema is nobody's file — it belongs to the
/// extension — so the scratch schema never had it, and a view calling it failed every diff with
/// 42883 even though apply, which resolves names in the target schema, finds it. The probe now
/// falls back to the target schema for whatever the desired state does not declare.
/// </summary>
/// <remarks>
/// One class on purpose: an extension is installed once per database, and xUnit runs the tests
/// of a class one at a time. Each test installs <c>pgcrypto</c> into its own schema, and dropping
/// that schema drops the extension with it.
/// </remarks>
public sealed class ViewProbeUndeclaredFunctionTests
{
    private static async Task<(PgTestSchema Live, string Url)> LiveWithPgcryptoAsync(string ddl)
    {
        var live = await PgTestSchema.CreateAsync("SELECT 1;");
        await PgTestSchema.ExecuteAsync($"""CREATE EXTENSION pgcrypto SCHEMA "{live.Name}";""");
        await PgTestSchema.ExecuteAsync($"""SET search_path TO "{live.Name}"; {ddl}""");
        var url = new NpgsqlConnectionStringBuilder(PgTestSchema.ServerUrl!) { SearchPath = live.Name }
            .ConnectionString;
        return (live, url);
    }

    [SkippableFact]
    public async Task A_view_calling_an_extension_function_is_probed_not_failed()
    {
        var (live, url) = await LiveWithPgcryptoAsync("""
            CREATE TABLE t (a int, b text);
            CREATE VIEW v AS SELECT a, encode(digest(b, 'sha256'), 'hex') AS h FROM t;
            """);
        await using var _ = live;

        var analysis = PgProgrammables.Analyze(new[]
        {
            new ProgrammableFile("v.sql", "CREATE VIEW v AS SELECT a, encode(digest(b, 'sha256'), 'hex') AS h, 1 AS c FROM t;"),
        });

        var refined = await ViewRedefinePlanner.RefineAsync(
            analysis, new[] { "CREATE TABLE t (a int, b text);" }, url, live.Name, CancellationToken.None);

        // Appending a column is provably safe — the probe answered, so the blanket warning is gone.
        var view = Assert.Single(refined.Objects);
        Assert.Null(view.RiskOverride);
        Assert.DoesNotContain("DROP VIEW", view.ApplyScript, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(refined.Messages);
    }

    [SkippableFact]
    public async Task A_view_calling_an_extension_function_survives_diff_and_apply_round_trips()
    {
        var (live, url) = await LiveWithPgcryptoAsync("SELECT 1;");
        await using var _ = live;
        var dir = Path.Combine(Path.GetTempPath(), "schemorph-pg-extfn-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(Path.Combine(dir, "tables"));
        Directory.CreateDirectory(Path.Combine(dir, "views"));
        var provider = new PostgresProvider();
        var ledger = new PostgresLedgerStore();

        async Task WriteAsync(string view)
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "tables", "orders.sql"),
                "CREATE TABLE orders (id int PRIMARY KEY, code text NOT NULL);");
            await File.WriteAllTextAsync(Path.Combine(dir, "views", "order_hash.sql"), view);
        }
        async Task ApplyAsync()
        {
            var outcome = await ApplyOperation.RunAsync(provider, ledger, new ApplyOperation.Request(dir, url));
            Assert.True(outcome.Success, string.Join("; ", outcome.Errors.Select(e => $"{e.Code}: {e.Text}")));
        }
        async Task<bool> HasChangesAsync()
        {
            var diff = await DiffOperation.RunAsync(provider, ledger, dir, url, false);
            Assert.True(diff.Success, string.Join("; ", diff.Errors.Select(e => $"{e.Code}: {e.Text}")));
            return diff.Plan!.HasChanges;
        }

        try
        {
            await WriteAsync("CREATE VIEW order_hash AS SELECT id, encode(digest(code, 'sha256'), 'hex') AS h FROM orders;");
            await ApplyAsync();
            Assert.False(await HasChangesAsync());

            await WriteAsync("CREATE VIEW order_hash AS SELECT id, encode(digest(code, 'sha256'), 'hex') AS h, length(code) AS n FROM orders;");
            Assert.True(await HasChangesAsync());
            await ApplyAsync();
            Assert.False(await HasChangesAsync());

            await using var connection = new NpgsqlConnection(url);
            await connection.OpenAsync();
            await new NpgsqlCommand("INSERT INTO orders VALUES (1, 'ab')", connection).ExecuteNonQueryAsync();
            Assert.Equal(2, (int)(await new NpgsqlCommand("SELECT n FROM order_hash WHERE id = 1", connection).ExecuteScalarAsync())!);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [SkippableFact]
    public async Task A_desired_table_still_wins_over_the_live_one_of_the_same_name()
    {
        // The target schema is the fallback, not a peer: a table the desired state declares must
        // be read in its desired shape, or a mid-list column insert would look like an append.
        await using var live = await PgTestSchema.CreateAsync("""
            CREATE TABLE t (a int, b int);
            CREATE VIEW v AS SELECT * FROM t;
            """);
        var url = new NpgsqlConnectionStringBuilder(PgTestSchema.ServerUrl!) { SearchPath = live.Name }
            .ConnectionString;

        var analysis = PgProgrammables.Analyze(new[]
        {
            new ProgrammableFile("v.sql", "CREATE VIEW v AS SELECT * FROM t;"),
        });

        var refined = await ViewRedefinePlanner.RefineAsync(
            analysis, new[] { "CREATE TABLE t (a int, c int, b int);" }, url, live.Name, CancellationToken.None);

        var view = Assert.Single(refined.Objects);
        Assert.StartsWith("DROP VIEW", view.ApplyScript, StringComparison.OrdinalIgnoreCase);
    }
}
