using Npgsql;
using Schemorph.Core.Providers;

namespace Schemorph.Provider.Postgres.Tests;

/// <summary>
/// What PostgreSQL 18 changed in the catalogs, read from a real 18 server: a virtual generated column
/// (<c>attgenerated = 'v'</c>) and NOT NULL stored in <c>pg_constraint</c>. The other tests in this project pin the
/// routing and the query text; these two are the ones that fail if the server says something the reader does
/// not understand. They skip on a server older than 18, where neither exists.
/// </summary>
public class Postgres18ServerReadTests
{
    private static async Task SkipUnlessServerIs18OrNewerAsync()
    {
        Skip.If(PostgresTestSchema.ServerUrl is null, "SCHEMORPH_PG_TEST_URL is not set; Postgres tests need a live server.");
        await using var connection = new NpgsqlConnection(PostgresTestSchema.ServerUrl);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SHOW server_version_num", connection);
        var version = int.Parse((string)(await command.ExecuteScalarAsync())!, System.Globalization.CultureInfo.InvariantCulture);
        Skip.If(version < 180000, $"The server is {version}; virtual generated columns and NOT NULL in pg_constraint are PostgreSQL 18.");
    }

    [SkippableFact]
    public async Task A_virtual_generated_column_is_written_as_virtual_and_the_output_applies()
    {
        await SkipUnlessServerIs18OrNewerAsync();
        await using var source = await PostgresTestSchema.CreateAsync(
            "CREATE TABLE t (a int NOT NULL, b int GENERATED ALWAYS AS (a * 2) VIRTUAL, c int GENERATED ALWAYS AS (a + 1) STORED);");
        var url = $"{PostgresTestSchema.ServerUrl};Search Path={source.Name}";

        var file = Assert.Single((await new PostgresProvider().InspectAsync(new InspectRequest(url))).Files);

        Assert.Contains("GENERATED ALWAYS AS ((a * 2)) VIRTUAL", file.Content);
        Assert.Contains("GENERATED ALWAYS AS ((a + 1)) STORED", file.Content);
        Assert.DoesNotContain("DEFAULT (a * 2)", file.Content);

        // The honest test of the rendering: the server accepts it. Written as a DEFAULT, the engine refuses it
        // ("cannot use column reference in DEFAULT expression").
        await using var target = await PostgresTestSchema.CreateAsync("SELECT 1;");
        await PostgresTestSchema.ExecuteAsync(file.Content.Replace(
            $"\"{source.Name}\".", $"\"{target.Name}\".", StringComparison.Ordinal));
        var reread = Assert.Single(await CatalogReader.ReadTablesAsync(PostgresTestSchema.ServerUrl!, target.Name));
        Assert.Equal(3, reread.Columns.Count);
    }

    [SkippableFact]
    public async Task A_not_null_column_comes_back_once_not_also_as_a_constraint_of_its_own()
    {
        await SkipUnlessServerIs18OrNewerAsync();
        await using var source = await PostgresTestSchema.CreateAsync(
            "CREATE TABLE t (a int NOT NULL, b int, c int CONSTRAINT c_named_not_null NOT NULL);");
        var url = $"{PostgresTestSchema.ServerUrl};Search Path={source.Name}";

        var file = Assert.Single((await new PostgresProvider().InspectAsync(new InspectRequest(url))).Files);
        var table = Assert.Single(await CatalogReader.ReadTablesAsync(PostgresTestSchema.ServerUrl!, source.Name));

        Assert.Empty(table.Constraints);
        Assert.DoesNotContain("ADD CONSTRAINT", file.Content);
        Assert.Contains("\"a\" integer NOT NULL", file.Content);
        Assert.Contains("\"c\" integer NOT NULL", file.Content);
    }
}
