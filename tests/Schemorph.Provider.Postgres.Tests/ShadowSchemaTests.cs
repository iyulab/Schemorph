using Schemorph.Provider.Postgres.Shadow;

namespace Schemorph.Provider.Postgres.Tests;

/// <summary>
/// The shadow harness end to end on a live server: inspect a source schema,
/// retarget its rendered desired state into a scratch schema, apply, and read
/// the scratch back through the same catalog reader. This is the machinery
/// cycle-76 deliberately left the independent-index round trip unproven for —
/// string substitution put the index in the SOURCE schema; the parser-based
/// rewrite must put it in the shadow.
/// </summary>
public class ShadowSchemaTests
{
    private const string Ddl = """
        CREATE TABLE "Workspaces" (
            "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
            "Name" text NOT NULL,
            "Tier" text NOT NULL,
            CONSTRAINT "PK_Workspaces" PRIMARY KEY ("Id"),
            CONSTRAINT "CK_Tier" CHECK ("Tier" IN ('free', 'pro'))
        );
        CREATE UNIQUE INDEX "IX_Workspaces_Lower" ON "Workspaces" (lower("Name"));
        """;

    [SkippableFact]
    public async Task The_rendered_desired_state_round_trips_through_a_shadow_schema()
    {
        await using var source = await PgTestSchema.CreateAsync(Ddl);
        var sourceTables = await CatalogReader.ReadTablesAsync(PgTestSchema.ServerUrl!, source.Name);
        var rendered = DesiredStateRenderer.Render(sourceTables);

        await using var shadow = await ShadowSchema.CreateAsync(PgTestSchema.ServerUrl!);
        await shadow.ApplyAsync(
            rendered.Select(f => f.Content).ToList(), sourceSchema: source.Name);

        var shadowTables = await CatalogReader.ReadTablesAsync(PgTestSchema.ServerUrl!, shadow.Name);

        var expected = Assert.Single(sourceTables);
        var actual = Assert.Single(shadowTables);
        Assert.Equal(expected.Name, actual.Name);
        Assert.Equal(
            expected.Columns,
            actual.Columns);   // engine-canonical renderings must agree exactly
        Assert.Equal(
            expected.Constraints.Select(c => (c.Name, c.Definition)),
            actual.Constraints.Select(c => (c.Name, c.Definition)));

        // The unproven case from cycle-76: the independent expression index must
        // exist in the SHADOW schema — with string substitution it stayed in the
        // source schema because pg_get_indexdef renders that qualifier unquoted.
        var index = Assert.Single(actual.Indexes);
        Assert.Equal("IX_Workspaces_Lower", index.Name);
        Assert.Contains(shadow.Name, index.CreateStatement);
        Assert.DoesNotContain(source.Name, index.CreateStatement);
    }

    [SkippableFact]
    public async Task The_scratch_schema_is_gone_after_disposal()
    {
        Skip.If(PgTestSchema.ServerUrl is null,
            "SCHEMORPH_PG_TEST_URL is not set; Postgres tests need a live server.");

        string name;
        await using (var shadow = await ShadowSchema.CreateAsync(PgTestSchema.ServerUrl!))
        {
            name = shadow.Name;
        }

        var leftover = await CatalogReader.ReadTablesAsync(PgTestSchema.ServerUrl!, name);
        Assert.Empty(leftover);
    }

    [SkippableFact]
    public async Task A_failing_statement_leaves_no_half_applied_desired_state()
    {
        await using var source = await PgTestSchema.CreateAsync(Ddl);
        var rendered = DesiredStateRenderer.Render(
            await CatalogReader.ReadTablesAsync(PgTestSchema.ServerUrl!, source.Name));

        await using var shadow = await ShadowSchema.CreateAsync(PgTestSchema.ServerUrl!);
        var sabotaged = rendered[0].Content + "\nCREATE INDEX \"IX_Dup\" ON \"Missing\" (\"x\");";

        await Assert.ThrowsAsync<Npgsql.PostgresException>(
            () => shadow.ApplyAsync([sabotaged], sourceSchema: source.Name));

        // One transaction per desired-state text: nothing from it may survive.
        Assert.Empty(await CatalogReader.ReadTablesAsync(PgTestSchema.ServerUrl!, shadow.Name));
    }
}
