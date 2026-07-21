using Schemorph.Core.Providers;

namespace Schemorph.Provider.Postgres.Tests;

public class InspectTests
{
    private const string Ddl = """
        CREATE TABLE "Workspaces" (
            "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
            "Name" text NOT NULL,
            CONSTRAINT "PK_Workspaces" PRIMARY KEY ("Id"),
            CONSTRAINT "UQ_Workspaces_Name" UNIQUE ("Name")
        );
        """;

    [SkippableFact]
    public async Task Inspect_renders_the_live_schema_as_desired_state_files()
    {
        await using var schema = await PgTestSchema.CreateAsync(Ddl);
        var url = $"{PgTestSchema.ServerUrl};Search Path={schema.Name}";

        var result = await new PostgresProvider().InspectAsync(new InspectRequest(url));

        var file = Assert.Single(result.Files);
        Assert.Equal($"tables/{schema.Name}.Workspaces.sql", file.RelativePath);
        Assert.Contains("CREATE TABLE", file.Content);
        Assert.Contains("\"Id\" uuid NOT NULL DEFAULT gen_random_uuid()", file.Content);
        Assert.Contains("ADD CONSTRAINT \"PK_Workspaces\" PRIMARY KEY", file.Content);
    }

    [SkippableFact]
    public async Task What_inspect_renders_can_be_applied_to_an_empty_schema()
    {
        // The honest test of a renderer: its output is valid SQL that recreates
        // what it read. Nothing in P0 applies anything, so this drives psql-level
        // execution directly — it verifies the rendering, not an apply path.
        await using var source = await PgTestSchema.CreateAsync(Ddl);
        var url = $"{PgTestSchema.ServerUrl};Search Path={source.Name}";
        var rendered = (await new PostgresProvider().InspectAsync(new InspectRequest(url))).Files;

        await using var target = await PgTestSchema.CreateAsync("SELECT 1;");
        foreach (var file in rendered)
        {
            // The rendering is schema-qualified against the source schema, so it
            // recreates the source's shape; applying it into a second schema
            // proves only that the statements parse and execute.
            await PgTestSchema.ExecuteAsync(file.Content.Replace(
                $"\"{source.Name}\".", $"\"{target.Name}\".", StringComparison.Ordinal));
        }

        var reread = await CatalogReader.ReadTablesAsync(PgTestSchema.ServerUrl!, target.Name);
        var table = Assert.Single(reread);
        Assert.Equal("Workspaces", table.Name);
        Assert.Equal(2, table.Columns.Count);
        Assert.Contains(table.Constraints, c => c.Name.StartsWith("PK_", StringComparison.Ordinal));
    }

    [SkippableFact]
    public async Task Inspect_is_no_longer_refused()
    {
        await using var schema = await PgTestSchema.CreateAsync(Ddl);
        var url = $"{PgTestSchema.ServerUrl};Search Path={schema.Name}";

        // The declared capability and the behaviour have to agree; this is the
        // other half of ProviderBoundaryTests.
        var result = await new PostgresProvider().InspectAsync(new InspectRequest(url));

        Assert.NotEmpty(result.Files);
        Assert.Contains("inspect", PostgresProvider.DeclaredCapabilities);
    }
}
