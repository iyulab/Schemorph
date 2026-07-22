using Schemorph.Provider.Postgres.Shadow;

namespace Schemorph.Provider.Postgres.Tests;

/// <summary>
/// The rewriter is what makes the scratch-schema shadow sound. The decisive
/// case is the one measurement refuted string substitution with (cycle-76):
/// <c>pg_get_indexdef</c> renders fold-safe schema qualifiers UNQUOTED, so a
/// textual rewrite keyed on the quoted form misses them.
/// </summary>
public class SchemaRewriterTests
{
    [Fact]
    public void Retargets_quoted_and_unquoted_qualifiers_alike()
    {
        // Exactly the catalog's own mixed rendering: unquoted fold-safe schema,
        // quoted PascalCase table.
        var sql = """CREATE UNIQUE INDEX "IX_Lower" ON src_test."Workspaces" USING btree (lower("Name"));""";

        var rewritten = SchemaRewriter.Retarget(sql, "src_test", "shadow_x");

        Assert.Contains("shadow_x", rewritten);
        Assert.DoesNotContain("src_test", rewritten);
        Assert.Contains("lower(\"Name\")", rewritten);   // expression untouched
    }

    [Fact]
    public void Retargets_every_schema_bearing_position_of_a_table()
    {
        var sql = """
            CREATE TABLE "Src"."Members" (
                "Id" uuid NOT NULL,
                "Role" "Src"."MemberRole" NOT NULL,
                "WorkspaceId" uuid REFERENCES "Src"."Workspaces" ("Id") ON DELETE CASCADE,
                "Slug" text DEFAULT "Src".make_slug()
            );
            """;

        var rewritten = SchemaRewriter.Retarget(sql, "Src", "shadow_x");

        Assert.DoesNotContain("\"Src\"", rewritten);
        // Table, FK target, qualified type, qualified function — all retargeted.
        Assert.Contains("shadow_x.\"Members\"", rewritten);
        Assert.Contains("shadow_x.\"Workspaces\"", rewritten);
        Assert.Contains("shadow_x.\"MemberRole\"", rewritten);
        Assert.Contains("shadow_x.make_slug()", rewritten);
    }

    [Fact]
    public void Qualified_column_references_are_retargeted()
    {
        // Tables do not carry three-part column refs, but the rewriter is
        // statement-agnostic and views (P3) will feed it exactly this shape.
        var sql = """CREATE VIEW "Src"."V" AS SELECT "Src"."T"."c" FROM "Src"."T";""";

        var rewritten = SchemaRewriter.Retarget(sql, "Src", "shadow_x");

        Assert.DoesNotContain("\"Src\"", rewritten);
    }

    [Fact]
    public void An_identifier_that_merely_looks_like_the_schema_is_left_alone()
    {
        // A column NAMED like the schema is not a schema reference. This is
        // why the rewrite is keyed on node type + field, never on string value.
        var sql = """CREATE TABLE "Src"."T" ("Src" text, "c" text DEFAULT 'Src');""";

        var rewritten = SchemaRewriter.Retarget(sql, "Src", "shadow_x");

        Assert.Contains("shadow_x.\"T\"", rewritten);
        Assert.Contains("\"Src\" text", rewritten);      // the column keeps its name
        Assert.Contains("'Src'", rewritten);             // the literal keeps its value
    }

    [Fact]
    public void References_to_other_schemas_pass_through_untouched()
    {
        var sql = """CREATE TABLE "Src"."T" ("r" uuid REFERENCES "Other"."U" ("Id"));""";

        var rewritten = SchemaRewriter.Retarget(sql, "Src", "shadow_x");

        Assert.Contains("\"Other\".\"U\"", rewritten);
    }

    [Fact]
    public void A_set_is_ordered_tables_then_constraints_then_foreign_keys_then_indexes()
    {
        // Desired-state files carry no reliable order (the first live corpus
        // arrived alphabetically: Members before the Workspaces it references).
        // The set is reordered like pg_dump's pre-data/post-data split.
        var members = """
            CREATE TABLE "Src"."Members" ("Id" uuid, "W" uuid);
            ALTER TABLE "Src"."Members" ADD CONSTRAINT "PK_M" PRIMARY KEY ("Id");
            ALTER TABLE "Src"."Members" ADD CONSTRAINT "FK_M_W" FOREIGN KEY ("W") REFERENCES "Src"."Workspaces" ("Id");
            CREATE INDEX "IX_M" ON "Src"."Members" ("W");
            """;
        var workspaces = """
            CREATE TABLE "Src"."Workspaces" ("Id" uuid);
            ALTER TABLE "Src"."Workspaces" ADD CONSTRAINT "PK_W" PRIMARY KEY ("Id");
            """;

        var sql = SchemaRewriter.RetargetSet([members, workspaces], "Src", "shadow_x");

        int Position(string marker) => sql.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(Position("CREATE TABLE shadow_x.\"Workspaces\"") < Position("\"PK_M\""));
        Assert.True(Position("\"PK_W\"") < Position("\"FK_M_W\""));
        Assert.True(Position("\"FK_M_W\"") < Position("CREATE INDEX"));
    }

    [Fact]
    public void Invalid_sql_throws_with_the_parsers_position()
    {
        var ex = Assert.Throws<SchemaRewriteException>(
            () => SchemaRewriter.Retarget("CREATE TABLE \"T\" (\"a\" int,,);", "Src", "shadow_x"));

        Assert.Contains("syntax error", ex.Message);
        Assert.True(ex.CursorPosition > 0);
    }
}
