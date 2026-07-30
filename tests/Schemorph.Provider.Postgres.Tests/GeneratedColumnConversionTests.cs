using Npgsql;
using Schemorph.Core.Operations;

namespace Schemorph.Provider.Postgres.Tests;

/// <summary>
/// A generated column's values look derived, and that makes it tempting to treat
/// the column as disposable: whatever is in it can be computed again. That stops
/// being true the moment the desired state drops the generation expression —
/// afterwards the values are ordinary data the database cannot recompute, and the
/// expression that produced them is gone from the schema. So the two directions
/// of this change are not symmetric, and only one of them can be a rebuild:
///
/// <list type="bullet">
/// <item>dropping an expression is <em>lossless in the engine</em>
/// (`ALTER COLUMN … DROP EXPRESSION`, PostgreSQL 13+) — the column becomes an
/// ordinary one and keeps every value;</item>
/// <item>gaining or changing one has no in-place form on this baseline
/// (PostgreSQL 16 has no `SET EXPRESSION`), so the column is dropped and added —
/// and that is honest, because the new values are the expression's output by
/// definition.</item>
/// </list>
///
/// These run the whole loop through the same core operations every surface
/// renders, so what they pin is the user-visible outcome, not a synthesizer
/// detail: the rows survive, and the next diff is empty.
/// </summary>
public class GeneratedColumnConversionTests : IAsyncLifetime
{
    private PgTestSchema _live = null!;
    private string _url = null!;
    private string _schemaDir = null!;
    private readonly PostgresProvider _provider = new();
    private readonly PostgresLedgerStore _ledger = new();

    // The live table: "total" is computed, and it already holds rows.
    private const string LiveV1 = """
        CREATE TABLE order_line (
            id integer NOT NULL,
            price numeric NOT NULL,
            qty integer NOT NULL,
            total numeric GENERATED ALWAYS AS (price * qty) STORED,
            CONSTRAINT pk_order_line PRIMARY KEY (id)
        );
        INSERT INTO order_line (id, price, qty) VALUES (1, 10, 2), (2, 30, 2);
        """;

    public async Task InitializeAsync()
    {
        _live = await PgTestSchema.CreateAsync(LiveV1);
        _url = new NpgsqlConnectionStringBuilder(PgTestSchema.ServerUrl!) { SearchPath = _live.Name }
            .ConnectionString;
        _schemaDir = Path.Combine(
            Path.GetTempPath(), "schemorph-pg-generated-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(Path.Combine(_schemaDir, "tables"));
    }

    public async Task DisposeAsync()
    {
        await _live.DisposeAsync();
        try { Directory.Delete(_schemaDir, recursive: true); } catch { }
    }

    private async Task DeclareAsync(string totalColumn) =>
        await File.WriteAllTextAsync(Path.Combine(_schemaDir, "tables", "order_line.sql"), $"""
            CREATE TABLE "{_live.Name}".order_line (
                id integer NOT NULL,
                price numeric NOT NULL,
                qty integer NOT NULL,
                {totalColumn},
                CONSTRAINT pk_order_line PRIMARY KEY (id)
            );
            """);

    // The defect: the conversion was synthesized as DROP COLUMN + ADD COLUMN, so
    // every computed value became NULL — data loss on a change whose declared
    // intent is "keep this column, stop computing it", and one the engine offers
    // a lossless form for.
    [SkippableFact]
    public async Task Dropping_a_generation_expression_keeps_the_values()
    {
        await DeclareAsync("total numeric");

        var plan = await ApplyAsync();

        // Nothing is re-created here, so the warning that says a column's values
        // are lost must stay silent — a warning on a lossless change teaches
        // reviewers to ignore the band.
        Assert.DoesNotContain(plan.Messages, m => m.Code == "SCHEMORPH107");
        Assert.Equal([20m, 60m], await TotalsAsync());
        Assert.Null(await GenerationExpressionAsync());
        await AssertConvergedAsync();
    }

    // The other direction has no in-place form here, so the column is rebuilt.
    // The values are the expression's output afterwards, which is what was asked
    // for — what must hold is that the loop still converges.
    // The other direction has no in-place form here, so the column is rebuilt.
    // The values are the expression's output afterwards, which is what was asked
    // for — but the plan entry is a table-level alter, so the only thing that can
    // tell a reviewer the column is being re-created is the lint code.
    [SkippableFact]
    public async Task Gaining_a_generation_expression_warns_that_the_column_is_re_created()
    {
        await DeclareAsync("total numeric");
        await ApplyAsync();

        await DeclareAsync("total numeric GENERATED ALWAYS AS (price * qty) STORED");
        var plan = await ApplyAsync();

        Assert.Contains(plan.Messages,
            m => m.Code == "SCHEMORPH107" && m.Severity == "Warning" && m.Text.Contains("order_line"));
        Assert.Equal([20m, 60m], await TotalsAsync());
        Assert.NotNull(await GenerationExpressionAsync());
        await AssertConvergedAsync();
    }

    private async Task<Schemorph.Core.Planning.Plan> ApplyAsync()
    {
        var diff = await DiffOperation.RunAsync(_provider, _ledger, _schemaDir, _url, allowDestructive: false);
        Assert.True(diff.Success, string.Join("; ", diff.Errors.Select(e => e.Text)));
        Assert.True(diff.Plan!.HasChanges);

        var apply = await ApplyOperation.RunAsync(_provider, _ledger,
            new ApplyOperation.Request(_schemaDir, _url, ExpectedPlanHash: null));
        Assert.True(apply.Success, string.Join("; ", apply.Errors.Select(e => e.Text)));
        return diff.Plan;
    }

    private async Task AssertConvergedAsync()
    {
        var rediff = await DiffOperation.RunAsync(_provider, _ledger, _schemaDir, _url, allowDestructive: false);
        Assert.True(rediff.Success, string.Join("; ", rediff.Errors.Select(e => e.Text)));
        Assert.False(rediff.Plan!.HasChanges, "the plan must empty after the apply");
    }

    private async Task<List<decimal>> TotalsAsync()
    {
        await using var connection = new NpgsqlConnection(_url);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT total FROM order_line ORDER BY id;", connection);

        var totals = new List<decimal>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            totals.Add(await reader.IsDBNullAsync(0) ? -1m : reader.GetDecimal(0));
        }
        return totals;
    }

    private async Task<string?> GenerationExpressionAsync()
    {
        await using var connection = new NpgsqlConnection(_url);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT pg_get_expr(d.adbin, d.adrelid)
            FROM pg_attribute a
            JOIN pg_class c ON c.oid = a.attrelid
            JOIN pg_namespace n ON n.oid = c.relnamespace
            LEFT JOIN pg_attrdef d ON d.adrelid = a.attrelid AND d.adnum = a.attnum
            WHERE n.nspname = @schema AND c.relname = 'order_line'
              AND a.attname = 'total' AND a.attgenerated <> '';
            """, connection);
        command.Parameters.AddWithValue("schema", _live.Name);

        return await command.ExecuteScalarAsync() as string;
    }
}
