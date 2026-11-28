using Schemorph.Provider.Postgres.Shadow;

namespace Schemorph.Provider.Postgres.Tests;

/// <summary>
/// The rewriter is what makes the scratch-schema shadow sound. The decisive
/// case is the one measurement refuted string substitution with:
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

        // The table and the FK target are desired tables — retargeted.
        Assert.Contains("shadow_x.\"Members\"", rewritten);
        Assert.Contains("shadow_x.\"Workspaces\"", rewritten);
        // A qualified type and a qualified function are not: the shadow holds the desired tables
        // only, so they keep resolving in the source schema, where apply will find them. Moved
        // into the shadow they named nothing (42704 / 42883 when the shadow was built).
        Assert.Contains("\"Src\".\"MemberRole\"", rewritten);
        Assert.Contains("\"Src\".make_slug()", rewritten);
        Assert.DoesNotContain("shadow_x.\"MemberRole\"", rewritten);
        Assert.DoesNotContain("shadow_x.make_slug", rewritten);
    }

    [Fact]
    public void Qualified_column_references_are_retargeted()
    {
        // Tables do not carry three-part column refs, but the rewriter is
        // statement-agnostic and views will feed it exactly this shape once declared.
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
    public void Tables_that_reference_each_other_get_the_key_that_closes_the_cycle_added_after_the_tables()
    {
        // A department points at its head, an employee at the department: no order of the two CREATE TABLEs satisfies
        // both inline keys. One key (the one that closes the cycle) is re-added by ALTER TABLE after both tables exist.
        var dept = """CREATE TABLE "Src"."Dept" ("Id" uuid PRIMARY KEY, "HeadId" uuid REFERENCES "Src"."Emp" ("Id"));""";
        var emp = """CREATE TABLE "Src"."Emp" ("Id" uuid PRIMARY KEY, "DeptId" uuid REFERENCES "Src"."Dept" ("Id"));""";

        var sql = SchemaRewriter.RetargetSet([dept, emp], "Src", "shadow_x");

        int Position(string marker) => sql.IndexOf(marker, StringComparison.Ordinal);
        Assert.Equal(1, CountOf(sql, "ALTER TABLE"));
        Assert.True(Position("CREATE TABLE shadow_x.\"Emp\"") < Position("CREATE TABLE shadow_x.\"Dept\""));
        Assert.True(Position("CREATE TABLE shadow_x.\"Dept\"") < Position("ALTER TABLE"));
        // The key that did not close the cycle stays where the user wrote it; the one that did is now an ALTER.
        var emptyCreate = sql[Position("CREATE TABLE shadow_x.\"Emp\"")..Position("CREATE TABLE shadow_x.\"Dept\"")];
        Assert.DoesNotContain("REFERENCES", emptyCreate);
        Assert.Contains("FOREIGN KEY (\"DeptId\")", sql[Position("ALTER TABLE")..]);
    }

    [Fact]
    public void A_named_table_level_key_that_closes_a_cycle_keeps_its_name()
    {
        var a = """CREATE TABLE "Src"."A" ("Id" uuid PRIMARY KEY, "BId" uuid, CONSTRAINT "FK_A_B" FOREIGN KEY ("BId") REFERENCES "Src"."B" ("Id"));""";
        var b = """CREATE TABLE "Src"."B" ("Id" uuid PRIMARY KEY, "AId" uuid, CONSTRAINT "FK_B_A" FOREIGN KEY ("AId") REFERENCES "Src"."A" ("Id"));""";

        var sql = SchemaRewriter.RetargetSet([a, b], "Src", "shadow_x");

        Assert.Equal(1, CountOf(sql, "ALTER TABLE"));
        Assert.Contains("\"FK_A_B\"", sql);
        Assert.Contains("\"FK_B_A\"", sql);
        Assert.True(sql.IndexOf("ALTER TABLE", StringComparison.Ordinal) > sql.LastIndexOf("CREATE TABLE", StringComparison.Ordinal));
    }

    [Fact]
    public void A_longer_cycle_is_broken_at_one_key_and_a_self_reference_stays_inline()
    {
        var a = """CREATE TABLE "Src"."A" ("Id" uuid PRIMARY KEY, "BId" uuid REFERENCES "Src"."B" ("Id"));""";
        var b = """CREATE TABLE "Src"."B" ("Id" uuid PRIMARY KEY, "CId" uuid REFERENCES "Src"."C" ("Id"));""";
        var c = """CREATE TABLE "Src"."C" ("Id" uuid PRIMARY KEY, "AId" uuid REFERENCES "Src"."A" ("Id"), "ParentId" uuid REFERENCES "Src"."C" ("Id"));""";

        var sql = SchemaRewriter.RetargetSet([a, b, c], "Src", "shadow_x");

        var alterAt = sql.IndexOf("ALTER TABLE", StringComparison.Ordinal);
        Assert.Equal(1, CountOf(sql, "ALTER TABLE"));
        // Before the ALTER: the cycle's two keys that stay inline and the self-reference; after it: the one key it re-adds.
        Assert.Equal(3, CountOf(sql[..alterAt], "REFERENCES"));
        Assert.Equal(1, CountOf(sql[alterAt..], "REFERENCES"));
    }

    [Fact]
    public void A_set_without_a_cycle_gets_no_alter()
    {
        var members = """CREATE TABLE "Src"."Members" ("Id" uuid PRIMARY KEY, "W" uuid REFERENCES "Src"."Workspaces" ("Id"));""";
        var workspaces = """CREATE TABLE "Src"."Workspaces" ("Id" uuid PRIMARY KEY);""";

        var sql = SchemaRewriter.RetargetSet([members, workspaces], "Src", "shadow_x");

        Assert.Equal(0, CountOf(sql, "ALTER TABLE"));
    }

    private static int CountOf(string text, string needle)
    {
        var count = 0;
        for (var i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = text.IndexOf(needle, i + needle.Length, StringComparison.Ordinal)) count++;
        return count;
    }

    [Fact]
    public void A_create_table_qualified_to_a_different_schema_than_the_connection_is_refused()
    {
        // The connection resolves the target schema as "public" (the default
        // when search_path carries something else, or is absent) while the
        // desired-state SQL is qualified to "app_control" — exactly the
        // mismatch that let a bare `diff` create real tables outside the
        // shadow sandbox. Passing this through untouched,
        // the way a REFERENCES target legitimately does, would execute the
        // CREATE TABLE for real against "app_control" the moment
        // ShadowSchema.ApplyAsync runs it on the live connection.
        var sql = """CREATE TABLE "app_control"."Apps" ("Id" uuid NOT NULL);""";

        var ex = Assert.Throws<SchemaRewriteException>(
            () => SchemaRewriter.Retarget(sql, "public", "shadow_x"));

        Assert.Contains("app_control", ex.Message);
        Assert.Contains("public", ex.Message);
        Assert.Contains("Apps", ex.Message);
    }

    [Fact]
    public void An_alter_table_qualified_to_a_different_schema_than_the_connection_is_refused()
    {
        var sql = """ALTER TABLE "app_control"."Apps" ADD COLUMN "Name" text;""";

        Assert.Throws<SchemaRewriteException>(
            () => SchemaRewriter.Retarget(sql, "public", "shadow_x"));
    }

    [Fact]
    public void An_index_qualified_to_a_different_schema_than_the_connection_is_refused()
    {
        var sql = """CREATE INDEX "IX_Apps" ON "app_control"."Apps" ("Name");""";

        Assert.Throws<SchemaRewriteException>(
            () => SchemaRewriter.Retarget(sql, "public", "shadow_x"));
    }

    [Fact]
    public void A_foreign_key_reference_to_another_schema_is_still_pass_through_not_refused()
    {
        // The statement's OWN target ("Src"."T") matches the connection's
        // resolved schema — only the REFERENCES target lives elsewhere, which
        // stays a legitimate cross-schema reference (unaffected by the new
        // guard; same fixture as References_to_other_schemas_pass_through_untouched).
        var sql = """CREATE TABLE "Src"."T" ("r" uuid REFERENCES "Other"."U" ("Id"));""";

        var rewritten = SchemaRewriter.Retarget(sql, "Src", "shadow_x");

        Assert.Contains("\"Other\".\"U\"", rewritten);
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
