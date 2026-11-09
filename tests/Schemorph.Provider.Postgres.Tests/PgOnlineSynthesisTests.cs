using Schemorph.Provider.Postgres.Shadow;

namespace Schemorph.Provider.Postgres.Tests;

/// <summary>
/// What the online stage (ADR-0009) moves out of the apply's transaction, and what it leaves in — judged
/// without a database, on the synthesized statements. (Running them against a real server is in the
/// provider's live tests.) The default path is pinned beside it: with the stage off, the output is the
/// output it always was.
/// </summary>
public class PgOnlineSynthesisTests
{
    private const string Quoted = """CREATE INDEX "IX_Doc_Name" ON "Doc" USING btree ("Name")""";
    private const string QuotedUnique = """CREATE UNIQUE INDEX "UX_Doc_Name" ON "Doc" USING btree ("Name")""";
    private const string Check = """CHECK (("Qty" >= 0))""";
    private const string ForeignKey = """FOREIGN KEY ("OtherId") REFERENCES "Other" ("Id")""";

    private static PgTable Table(
        string name = "Doc", IReadOnlyList<PgConstraint>? constraints = null, IReadOnlyList<PgIndex>? indexes = null) => new(
        "s", name,
        [new PgColumn("Id", "integer", true, null), new PgColumn("Qty", "integer", false, null)],
        constraints ?? [], indexes ?? []);

    private static IReadOnlyList<DdlSynthesizer.Statement> Online(
        IReadOnlyList<PgTable> desired, IReadOnlyList<PgTable> live)
        => DdlSynthesizer.Synthesize("s", desired, live, online: true);

    private static IReadOnlyList<string> Transactional(IReadOnlyList<DdlSynthesizer.Statement> statements)
        => statements.Where(s => !s.Online).Select(s => s.Sql).ToList();

    private static IReadOnlyList<string> OnlineOnly(IReadOnlyList<DdlSynthesizer.Statement> statements)
        => statements.Where(s => s.Online).Select(s => s.Sql).ToList();

    // ---- the default path is untouched

    [Fact]
    public void With_the_stage_off_a_rich_change_synthesizes_exactly_what_it_always_did()
    {
        // The expected list is what the synthesizer produced for this scenario at the commit BEFORE the
        // online stage existed (captured by running the scenario against that commit) - a record of the old
        // behaviour, not a restatement of the new code.
        var (desired, live) = OnlineGoldenScenario.Build();

        var statements = DdlSynthesizer.Synthesize("s", desired, live);

        Assert.All(statements, s => Assert.False(s.Online));
        Assert.Equal(
            [
                """ALTER TABLE "s"."Doc" DROP CONSTRAINT "CK_old";""",
                """DROP INDEX "s"."IX_Doc_Name";""",
                """DROP INDEX "s"."IX_gone";""",
                """DROP INDEX "s"."IX_bad";""",
                """CREATE TABLE "s"."Fresh" ("Id" integer NOT NULL, "Qty" integer);""",
                """ALTER TABLE "s"."Doc" VALIDATE CONSTRAINT "CK_wait";""",
                """ALTER TABLE "s"."Doc" ADD CONSTRAINT "CK_new" CHECK (("Qty" > 1));""",
                """ALTER TABLE "s"."Doc" ADD CONSTRAINT "PK_Doc" PRIMARY KEY ("Id");""",
                """ALTER TABLE "s"."Fresh" ADD CONSTRAINT "CK_f" CHECK (("Qty" >= 0));""",
                """CREATE INDEX "IX_Doc_Name" ON "Doc" USING btree ("Qty");""",
                """CREATE INDEX "IX_bad" ON "Doc" USING btree ("Name");""",
                """CREATE UNIQUE INDEX "UX_new" ON "Doc" USING btree ("Name");""",
                """CREATE INDEX "IX_f" ON "Fresh" USING btree ("Name");""",
                """ALTER TABLE "s"."Doc" ADD CONSTRAINT "FK_new" FOREIGN KEY ("OtherId") REFERENCES "Other" ("Id");""",
                """DROP TABLE "s"."Retired";""",
            ],
            statements.Select(s => s.Sql).ToList());
    }

    [Fact]
    public void With_the_stage_on_the_same_change_keeps_every_transactional_statement_it_has_no_reason_to_move()
    {
        var (desired, live) = OnlineGoldenScenario.Build();

        var statements = DdlSynthesizer.Synthesize("s", desired, live, online: true);

        // Moved: the three builds on the existing table, the replaced ones' drops, the two validations.
        // Everything else - the created table and all that hangs off it, the PRIMARY KEY, the drops of what
        // is no longer declared, the table drop - is exactly where it was.
        var kept = DdlSynthesizer.Synthesize("s", desired, live).Select(s => s.Sql).ToList();
        foreach (var sql in Transactional(statements).Where(sql => !sql.Contains("NOT VALID")))
        {
            Assert.Contains(sql, kept);
        }
        Assert.Contains(Transactional(statements), s => s.Contains("""CREATE TABLE "s"."Fresh" """.Trim()));
        Assert.Contains(Transactional(statements), s => s.Contains("PK_Doc") && !s.Contains("NOT VALID"));
        Assert.Contains(Transactional(statements), s => s == """ALTER TABLE "s"."Fresh" ADD CONSTRAINT "CK_f" CHECK (("Qty" >= 0));""");
        Assert.Contains(Transactional(statements), s => s.StartsWith("""CREATE INDEX "IX_f" ON "Fresh" """));
        Assert.Contains(Transactional(statements), s => s == """DROP INDEX "s"."IX_gone";""");
        Assert.Contains(Transactional(statements), s => s == """DROP TABLE "s"."Retired";""");
        Assert.Equal(
            [
                "DROP INDEX CONCURRENTLY \"s\".\"IX_Doc_Name\";",
                "CREATE INDEX CONCURRENTLY \"IX_Doc_Name\" ON \"Doc\" USING btree (\"Qty\");",
                "DROP INDEX CONCURRENTLY \"s\".\"IX_bad\";",
                "CREATE INDEX CONCURRENTLY \"IX_bad\" ON \"Doc\" USING btree (\"Name\");",
                "CREATE UNIQUE INDEX CONCURRENTLY \"UX_new\" ON \"Doc\" USING btree (\"Name\");",
                "ALTER TABLE \"s\".\"Doc\" VALIDATE CONSTRAINT \"CK_wait\";",
                "ALTER TABLE \"s\".\"Doc\" VALIDATE CONSTRAINT \"FK_new\";",
                "ALTER TABLE \"s\".\"Doc\" VALIDATE CONSTRAINT \"CK_new\";",
            ],
            OnlineOnly(statements));
    }

    // ---- indexes

    [Fact]
    public void A_new_index_on_an_existing_table_is_built_concurrently_after_the_commit()
    {
        var statements = Online([Table(indexes: [new PgIndex("IX_Doc_Name", Quoted)])], [Table()]);

        Assert.Empty(Transactional(statements));
        var build = Assert.Single(OnlineOnly(statements));
        Assert.Equal("""CREATE INDEX CONCURRENTLY "IX_Doc_Name" ON "Doc" USING btree ("Name");""", build);
        Assert.Equal("Doc", Assert.Single(statements).ObjectName);
    }

    [Fact]
    public void A_unique_index_is_built_concurrently_too()
    {
        var statements = Online([Table(indexes: [new PgIndex("UX_Doc_Name", QuotedUnique)])], [Table()]);

        Assert.Equal("""CREATE UNIQUE INDEX CONCURRENTLY "UX_Doc_Name" ON "Doc" USING btree ("Name");""",
            Assert.Single(OnlineOnly(statements)));
    }

    [Fact]
    public void A_changed_index_is_dropped_and_built_concurrently_in_that_order_and_never_dropped_in_the_transaction()
    {
        var live = Table(indexes: [new PgIndex("IX_Doc_Name", Quoted)]);
        var wanted = Table(indexes: [new PgIndex("IX_Doc_Name", Quoted.Replace("\"Name\"", "\"Qty\""))]);

        var statements = Online([wanted], [live]);

        Assert.Empty(Transactional(statements));
        var online = OnlineOnly(statements);
        Assert.Equal(2, online.Count);
        Assert.Equal("DROP INDEX CONCURRENTLY \"s\".\"IX_Doc_Name\";", online[0]);
        Assert.StartsWith("CREATE INDEX CONCURRENTLY \"IX_Doc_Name\"", online[1]);
    }

    [Fact]
    public void An_invalid_index_is_rebuilt_the_same_way()
    {
        var live = Table(indexes: [new PgIndex("IX_Doc_Name", Quoted, IsValid: false)]);

        var statements = Online([Table(indexes: [new PgIndex("IX_Doc_Name", Quoted)])], [live]);

        Assert.Empty(Transactional(statements));
        Assert.Equal(2, OnlineOnly(statements).Count);
        Assert.StartsWith("DROP INDEX CONCURRENTLY", OnlineOnly(statements)[0]);
    }

    [Fact]
    public void An_index_the_desired_state_no_longer_declares_is_still_dropped_in_the_transaction()
    {
        var statements = Online([Table()], [Table(indexes: [new PgIndex("IX_Doc_Name", Quoted)])]);

        Assert.Equal("DROP INDEX \"s\".\"IX_Doc_Name\";", Assert.Single(Transactional(statements)));
        Assert.Empty(OnlineOnly(statements));
    }

    [Fact]
    public void An_index_on_a_table_this_apply_creates_is_built_in_the_transaction()
    {
        var statements = Online([Table(indexes: [new PgIndex("IX_Doc_Name", Quoted)])], []);

        Assert.Empty(OnlineOnly(statements));
        Assert.Contains(Transactional(statements), s => s.StartsWith("CREATE INDEX \"IX_Doc_Name\""));
    }

    [Fact]
    public void An_index_whose_text_is_not_in_the_shape_the_engine_writes_stays_where_it_was()
    {
        var statements = Online([Table(indexes: [new PgIndex("IX_odd", "SOMETHING ELSE ON Doc")])], [Table()]);

        Assert.Empty(OnlineOnly(statements));
        Assert.Single(Transactional(statements));
    }

    [Fact]
    public void A_unique_index_a_foreign_key_added_in_the_same_plan_points_at_stays_in_the_transaction()
    {
        // The key needs the unique index to exist at the moment it is added, NOT VALID or not.
        var referenced = Table("Other", indexes: [new PgIndex("UX_Other_Id", QuotedUnique.Replace("\"Doc\"", "\"Other\"").Replace("UX_Doc_Name", "UX_Other_Id"))]);
        var referencing = Table(constraints: [new PgConstraint("FK_Doc_Other", ForeignKey)]);

        var statements = Online([referenced, referencing], [Table("Other"), Table()]);

        Assert.Contains(Transactional(statements), s => s.StartsWith("CREATE UNIQUE INDEX \"UX_Other_Id\""));
        Assert.DoesNotContain(OnlineOnly(statements), s => s.Contains("UX_Other_Id"));
        // The key itself is added NOT VALID and validated after the commit — only the index it needs stayed.
        Assert.Contains(Transactional(statements), s => s.EndsWith("NOT VALID;") && s.Contains("FK_Doc_Other"));
        Assert.Contains(OnlineOnly(statements), s => s.Contains("VALIDATE CONSTRAINT \"FK_Doc_Other\""));
    }

    [Fact]
    public void A_plain_index_on_the_referenced_table_and_a_unique_index_elsewhere_still_go_online()
    {
        var referenced = Table("Other", indexes: [new PgIndex("IX_Other_Q", """CREATE INDEX "IX_Other_Q" ON "Other" USING btree ("Qty")""")]);
        var referencing = Table(
            constraints: [new PgConstraint("FK_Doc_Other", ForeignKey)],
            indexes: [new PgIndex("UX_Doc_Name", QuotedUnique)]);

        var statements = Online([referenced, referencing], [Table("Other"), Table()]);

        Assert.Contains(OnlineOnly(statements), s => s.Contains("CONCURRENTLY \"IX_Other_Q\""));
        Assert.Contains(OnlineOnly(statements), s => s.Contains("CONCURRENTLY \"UX_Doc_Name\""));
    }

    // ---- constraints

    [Theory]
    [InlineData("CK_Doc_Qty", Check)]
    [InlineData("FK_Doc_Other", ForeignKey)]
    public void A_check_or_foreign_key_added_to_an_existing_table_is_added_not_valid_and_validated_online(string name, string definition)
    {
        var statements = Online([Table(constraints: [new PgConstraint(name, definition)])], [Table()]);

        Assert.Equal($"ALTER TABLE \"s\".\"Doc\" ADD CONSTRAINT \"{name}\" {definition} NOT VALID;",
            Assert.Single(Transactional(statements)));
        Assert.Equal($"ALTER TABLE \"s\".\"Doc\" VALIDATE CONSTRAINT \"{name}\";", Assert.Single(OnlineOnly(statements)));
    }

    [Theory]
    [InlineData("PK_Doc", """PRIMARY KEY ("Id")""")]
    [InlineData("UQ_Doc_Qty", """UNIQUE ("Qty")""")]
    [InlineData("EX_Doc", """EXCLUDE USING gist ("Qty" WITH =)""")]
    public void A_constraint_with_no_not_valid_form_is_added_in_the_transaction_as_before(string name, string definition)
    {
        var statements = Online([Table(constraints: [new PgConstraint(name, definition)])], [Table()]);

        Assert.Equal($"ALTER TABLE \"s\".\"Doc\" ADD CONSTRAINT \"{name}\" {definition};",
            Assert.Single(Transactional(statements)));
        Assert.Empty(OnlineOnly(statements));
    }

    [Fact]
    public void A_constraint_on_a_table_this_apply_creates_is_added_valid_in_the_transaction()
    {
        var statements = Online([Table(constraints: [new PgConstraint("CK_Doc_Qty", Check)])], []);

        Assert.Empty(OnlineOnly(statements));
        Assert.DoesNotContain(Transactional(statements), s => s.Contains("NOT VALID"));
    }

    [Fact]
    public void A_validation_the_database_still_owes_is_an_online_statement_when_the_stage_is_on_and_a_transactional_one_otherwise()
    {
        var wanted = Table(constraints: [new PgConstraint("CK_Doc_Qty", Check)]);
        var live = Table(constraints: [new PgConstraint("CK_Doc_Qty", Check + " NOT VALID")]);

        var on = Online([wanted], [live]);
        var off = DdlSynthesizer.Synthesize("s", [wanted], [live]);

        Assert.Empty(Transactional(on));
        Assert.Equal("ALTER TABLE \"s\".\"Doc\" VALIDATE CONSTRAINT \"CK_Doc_Qty\";", Assert.Single(OnlineOnly(on)));
        Assert.Equal("ALTER TABLE \"s\".\"Doc\" VALIDATE CONSTRAINT \"CK_Doc_Qty\";", Assert.Single(off).Sql);
        Assert.False(Assert.Single(off).Online);
    }

    [Fact]
    public void A_constraint_whose_definition_changed_is_dropped_in_the_transaction_then_added_not_valid_and_validated_online()
    {
        var live = Table(constraints: [new PgConstraint("CK_Doc_Qty", Check)]);
        var wanted = Table(constraints: [new PgConstraint("CK_Doc_Qty", """CHECK (("Qty" >= 1))""")]);

        var statements = Online([wanted], [live]);

        var transactional = Transactional(statements);
        Assert.Equal(2, transactional.Count);
        Assert.Contains("DROP CONSTRAINT \"CK_Doc_Qty\"", transactional[0]);
        Assert.EndsWith("NOT VALID;", transactional[1]);
        Assert.Contains("VALIDATE CONSTRAINT \"CK_Doc_Qty\"", Assert.Single(OnlineOnly(statements)));
    }

    // ---- order

    [Fact]
    public void Every_online_statement_comes_after_every_transactional_one_and_builds_come_before_validations()
    {
        var wanted = Table(
            constraints: [new PgConstraint("CK_Doc_Qty", Check)],
            indexes: [new PgIndex("IX_Doc_Name", Quoted)]);
        var created = Table("Fresh", [new PgConstraint("CK_f", Check)]);

        var statements = Online([wanted, created], [Table()]);

        var firstOnline = statements.ToList().FindIndex(s => s.Online);
        Assert.True(firstOnline > 0);
        Assert.All(statements.Skip(firstOnline), s => Assert.True(s.Online));
        var online = OnlineOnly(statements);
        Assert.StartsWith("CREATE INDEX CONCURRENTLY", online[0]);
        Assert.StartsWith("ALTER TABLE", online[1]);
    }

    [Fact]
    public void The_stage_asks_nothing_of_a_schema_with_nothing_to_move()
    {
        var table = Table(constraints: [new PgConstraint("PK_Doc", """PRIMARY KEY ("Id")""")]);

        Assert.Empty(Online([table], [table]));
    }

    // ---- reading the table a key points at

    [Theory]
    [InlineData("""FOREIGN KEY ("a") REFERENCES "Other" ("Id")""", "Other")]
    [InlineData("""FOREIGN KEY (a) REFERENCES other(id)""", "other")]
    [InlineData("""FOREIGN KEY (a) REFERENCES "we""ird"(id) ON DELETE CASCADE""", "we\"ird")]
    [InlineData("""FOREIGN KEY (a) REFERENCES "Order Items" ("Id") DEFERRABLE""", "Order Items")]
    [InlineData("""FOREIGN KEY (a) REFERENCES other_schema.other(id)""", null)]
    [InlineData("""FOREIGN KEY (a) REFERENCES "Other"."T" ("Id")""", null)]
    [InlineData("""CHECK ((a > 0))""", null)]
    public void The_referenced_table_is_read_from_the_engines_text(string definition, string? expected)
        => Assert.Equal(expected, DdlSynthesizer.ReferencedTable(definition));
}
