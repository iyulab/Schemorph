namespace Schemorph.Provider.Postgres.Tests;

public class DesiredStateRendererTests
{
    private static PgTable Workspaces() => new(
        "vibebase_control", "Workspaces",
        Columns:
        [
            new PgColumn("Id", "uuid", NotNull: true, Default: "gen_random_uuid()"),
            new PgColumn("Name", "text", NotNull: true, Default: null),
            new PgColumn("CreatedAt", "timestamp with time zone", NotNull: true, Default: "now()"),
        ],
        Constraints:
        [
            new PgConstraint("PK_Workspaces", "PRIMARY KEY (\"Id\")"),
            new PgConstraint("CK_Workspaces_Status", "CHECK ((\"Status\" = ANY (ARRAY['active'::text, 'suspended'::text])))"),
        ],
        Indexes: [new PgIndex("IX_Workspaces_Name", "CREATE UNIQUE INDEX \"IX_Workspaces_Name\" ON vibebase_control.\"Workspaces\" USING btree (\"Name\")")]);

    [Fact]
    public void One_file_per_table_under_the_conventional_layout()
    {
        var files = DesiredStateRenderer.Render([Workspaces()]);

        var file = Assert.Single(files);
        Assert.Equal("tables/vibebase_control.Workspaces.sql", file.RelativePath);
    }

    [Fact]
    public void Every_identifier_is_quoted()
    {
        // Postgres folds unquoted identifiers to lower case, so a PascalCase
        // schema that renders unquoted does not round-trip. This is the same
        // defect that eliminated psqldef in the engine spike.
        var sql = DesiredStateRenderer.Render([Workspaces()])[0].Content;

        Assert.Contains("CREATE TABLE \"vibebase_control\".\"Workspaces\"", sql);
        Assert.Contains("\"Id\" uuid NOT NULL", sql);
        Assert.DoesNotContain("CREATE TABLE vibebase_control", sql);
    }

    [Fact]
    public void Defaults_and_nullability_render_verbatim_from_the_catalog()
    {
        var sql = DesiredStateRenderer.Render([Workspaces()])[0].Content;

        Assert.Contains("\"Id\" uuid NOT NULL DEFAULT gen_random_uuid()", sql);
        Assert.Contains("\"Name\" text NOT NULL,", sql);
        Assert.Contains("\"CreatedAt\" timestamp with time zone NOT NULL DEFAULT now()", sql);
    }

    [Fact]
    public void Constraints_are_attached_to_their_table_file()
    {
        // Each file is a complete, self-applicable desired state — the same rule
        // the SQL Server renderer follows by folding constraints into the table.
        var sql = DesiredStateRenderer.Render([Workspaces()])[0].Content;

        Assert.Contains(
            "ALTER TABLE \"vibebase_control\".\"Workspaces\" ADD CONSTRAINT \"PK_Workspaces\" PRIMARY KEY (\"Id\");",
            sql);
        Assert.Contains("ADD CONSTRAINT \"CK_Workspaces_Status\" CHECK", sql);
    }

    [Fact]
    public void Index_statements_are_emitted_as_the_catalog_gave_them()
    {
        var sql = DesiredStateRenderer.Render([Workspaces()])[0].Content;

        Assert.Contains("CREATE UNIQUE INDEX \"IX_Workspaces_Name\"", sql);
        Assert.EndsWith(";\n", sql.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void No_TSql_batch_separators_leak_into_Postgres_output()
    {
        var sql = DesiredStateRenderer.Render([Workspaces()])[0].Content;

        Assert.DoesNotContain("GO", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void An_embedded_quote_in_an_identifier_is_doubled()
    {
        var table = new PgTable("public", "we\"ird", [new PgColumn("c", "text", false, null)], [], []);

        var sql = DesiredStateRenderer.Render([table])[0].Content;

        Assert.Contains("\"we\"\"ird\"", sql);
    }

    [Fact]
    public void A_table_with_no_columns_still_renders_valid_sql()
    {
        // Postgres allows zero-column tables; rendering must not emit a dangling
        // comma or an empty parenthesis pair that fails to parse.
        var table = new PgTable("public", "Empty", [], [], []);

        var sql = DesiredStateRenderer.Render([table])[0].Content;

        Assert.Contains("CREATE TABLE \"public\".\"Empty\" ();", sql);
    }
}
