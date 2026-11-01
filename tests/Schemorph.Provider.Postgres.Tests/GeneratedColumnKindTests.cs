using Schemorph.Provider.Postgres.Shadow;

namespace Schemorph.Provider.Postgres.Tests;

/// <summary>
/// PostgreSQL 18 made a generated column virtual unless it says STORED. The catalog
/// marks the two with <c>attgenerated</c> (<c>'s'</c> / <c>'v'</c>) and keeps both
/// expressions in the DEFAULT slot, so a reader that knows only <c>'s'</c> turns a
/// virtual column into <c>DEFAULT (expr)</c>. None of this needs a server: the
/// routing, the rendering and the synthesis are plain functions of the model.
/// </summary>
public class GeneratedColumnKindTests
{
    private const string Expression = "(\"Price\" * \"Qty\")";

    [Theory]
    [InlineData('s', PgGeneratedKind.Stored)]
    [InlineData('v', PgGeneratedKind.Virtual)]
    public void A_generated_marker_routes_the_expression_to_the_generation_slot(char marker, PgGeneratedKind kind)
    {
        var (defaultExpression, generatedAs, routedKind) = CatalogReader.RouteExpression(marker, Expression);

        Assert.Null(defaultExpression);
        Assert.Equal(Expression, generatedAs);
        Assert.Equal(kind, routedKind);
    }

    [Theory]
    [InlineData('\0')]
    [InlineData(' ')]
    public void No_generated_marker_leaves_the_expression_a_default(char marker)
    {
        var (defaultExpression, generatedAs, _) = CatalogReader.RouteExpression(marker, "now()");

        Assert.Equal("now()", defaultExpression);
        Assert.Null(generatedAs);
    }

    [Theory]
    [InlineData(PgGeneratedKind.Stored, "STORED")]
    [InlineData(PgGeneratedKind.Virtual, "VIRTUAL")]
    public void A_generated_column_renders_the_kind_it_has(PgGeneratedKind kind, string keyword)
    {
        var table = new PgTable("public", "Prices",
            [new PgColumn("Total", "numeric", NotNull: false, Default: null, GeneratedAs: Expression, GeneratedKind: kind)],
            [], []);

        var sql = DesiredStateRenderer.Render([table])[0].Content;

        Assert.Contains($"GENERATED ALWAYS AS ({Expression}) {keyword}", sql);
        Assert.DoesNotContain("DEFAULT", sql);
    }

    [Fact]
    public void A_virtual_column_read_from_the_catalog_renders_back_as_virtual_not_as_a_default()
    {
        var (defaultExpression, generatedAs, kind) = CatalogReader.RouteExpression('v', Expression);
        var table = new PgTable("public", "Prices",
            [new PgColumn("Total", "numeric", NotNull: false, defaultExpression, GeneratedAs: generatedAs, GeneratedKind: kind)],
            [], []);

        var sql = DesiredStateRenderer.Render([table])[0].Content;

        Assert.Contains("VIRTUAL", sql);
        Assert.DoesNotContain("DEFAULT", sql);
    }

    [Fact]
    public void The_same_expression_kept_the_other_way_is_a_different_column()
    {
        var stored = new PgColumn("Total", "numeric", false, null, GeneratedAs: Expression, GeneratedKind: PgGeneratedKind.Stored);
        var virtualColumn = stored with { GeneratedKind = PgGeneratedKind.Virtual };

        Assert.NotEqual(stored, virtualColumn);
    }

    [Fact]
    public void Changing_only_the_kind_recreates_the_column()
    {
        PgTable Table(PgGeneratedKind kind) => new("public", "Prices",
            [new PgColumn("Price", "numeric", true, null),
             new PgColumn("Total", "numeric", false, null, GeneratedAs: Expression, GeneratedKind: kind)],
            [], []);

        var statements = DdlSynthesizer.Synthesize(
            "public", [Table(PgGeneratedKind.Virtual)], [Table(PgGeneratedKind.Stored)]);

        Assert.Collection(statements,
            s => Assert.Equal("ALTER TABLE \"public\".\"Prices\" DROP COLUMN \"Total\";", s.Sql),
            s => Assert.Equal(
                $"ALTER TABLE \"public\".\"Prices\" ADD COLUMN \"Total\" numeric GENERATED ALWAYS AS ({Expression}) VIRTUAL;",
                s.Sql));
        Assert.All(statements, s => Assert.False(s.LosesData));
    }

    [Fact]
    public void An_unchanged_virtual_column_plans_nothing()
    {
        PgTable Table() => new("public", "Prices",
            [new PgColumn("Price", "numeric", true, null),
             new PgColumn("Total", "numeric", false, null, GeneratedAs: Expression, GeneratedKind: PgGeneratedKind.Virtual)],
            [], []);

        Assert.Empty(DdlSynthesizer.Synthesize("public", [Table()], [Table()]));
    }
}
