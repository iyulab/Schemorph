using Schemorph.Provider.Postgres.Shadow;

namespace Schemorph.Provider.Postgres.Tests;

/// <summary>
/// Two things a PostgreSQL database can hold that read like the declared object but are not it: an
/// index a failed concurrent build left <em>invalid</em>, and a constraint added <c>NOT VALID</c>
/// and never validated. Without a database: how the snapshot comparison and the synthesized script
/// treat each. (Applying them to a real server and converging is in
/// <see cref="PostgresInvalidStateConvergenceTests"/>.)
/// </summary>
public class PostgresInvalidStateSynthesisTests
{
    private const string IndexDefinition = """CREATE UNIQUE INDEX "IX_Doc_Name" ON "Doc" USING btree ("Name")""";

    private static PostgresTable Table(
        IReadOnlyList<PostgresConstraint>? constraints = null, IReadOnlyList<PostgresIndex>? indexes = null) => new(
        "s", "Doc",
        [new PostgresColumn("Id", "integer", true, null), new PostgresColumn("Qty", "integer", false, null)],
        constraints ?? [], indexes ?? []);

    private static IReadOnlyList<string> Script(PostgresTable desired, PostgresTable live)
        => DdlSynthesizer.Synthesize("s", [desired], [live]).Select(s => s.Sql).ToList();

    // ---- indexes

    [Fact]
    public void An_invalid_live_index_is_a_difference_even_though_its_definition_reads_the_same()
    {
        var wanted = Table(indexes: [new PostgresIndex("IX_Doc_Name", IndexDefinition)]);
        var live = Table(indexes: [new PostgresIndex("IX_Doc_Name", IndexDefinition, IsValid: false)]);

        Assert.Single(SnapshotComparer.Compare([wanted], [live]));
    }

    [Fact]
    public void An_invalid_live_index_is_dropped_and_created_again()
    {
        var wanted = Table(indexes: [new PostgresIndex("IX_Doc_Name", IndexDefinition)]);
        var live = Table(indexes: [new PostgresIndex("IX_Doc_Name", IndexDefinition, IsValid: false)]);

        var script = Script(wanted, live);

        Assert.Equal(2, script.Count);
        Assert.StartsWith("DROP INDEX", script[0]);
        Assert.Contains("\"IX_Doc_Name\"", script[0]);
        Assert.StartsWith("CREATE UNIQUE INDEX", script[1]);
    }

    [Fact]
    public void A_valid_index_of_the_same_definition_plans_nothing()
    {
        var index = new PostgresIndex("IX_Doc_Name", IndexDefinition);

        Assert.Empty(SnapshotComparer.Compare([Table(indexes: [index])], [Table(indexes: [index])]));
        Assert.Empty(Script(Table(indexes: [index]), Table(indexes: [index])));
    }

    [Fact]
    public void An_invalid_index_the_desired_state_no_longer_declares_is_only_dropped()
    {
        var live = Table(indexes: [new PostgresIndex("IX_Doc_Name", IndexDefinition, IsValid: false)]);

        var script = Script(Table(), live);

        var drop = Assert.Single(script);
        Assert.StartsWith("DROP INDEX", drop);
    }

    // ---- constraints

    [Fact]
    public void A_constraint_that_only_lacks_validation_is_validated_in_place()
    {
        var wanted = Table([new PostgresConstraint("CK_Doc_Qty", """CHECK (("Qty" >= 0))""")]);
        var live = Table([new PostgresConstraint("CK_Doc_Qty", """CHECK (("Qty" >= 0)) NOT VALID""")]);

        Assert.Single(SnapshotComparer.Compare([wanted], [live]));

        var script = Script(wanted, live);

        var validate = Assert.Single(script);
        Assert.Equal("ALTER TABLE \"s\".\"Doc\" VALIDATE CONSTRAINT \"CK_Doc_Qty\";", validate);
    }

    [Fact]
    public void A_foreign_key_that_only_lacks_validation_is_validated_after_the_other_additions()
    {
        var foreignKey = """FOREIGN KEY ("Qty") REFERENCES "Other" ("Id")""";
        var wanted = Table([new PostgresConstraint("FK_Doc_Other", foreignKey), new PostgresConstraint("CK_New", """CHECK (("Qty" > 0))""")]);
        var live = Table([new PostgresConstraint("FK_Doc_Other", foreignKey + " NOT VALID")]);

        var script = Script(wanted, live);

        Assert.Equal(2, script.Count);
        Assert.Contains("ADD CONSTRAINT \"CK_New\"", script[0]);
        Assert.Contains("VALIDATE CONSTRAINT \"FK_Doc_Other\"", script[1]);
    }

    [Fact]
    public void A_not_valid_constraint_that_also_differs_in_what_it_checks_is_replaced()
    {
        var wanted = Table([new PostgresConstraint("CK_Doc_Qty", """CHECK (("Qty" >= 1))""")]);
        var live = Table([new PostgresConstraint("CK_Doc_Qty", """CHECK (("Qty" >= 0)) NOT VALID""")]);

        var script = Script(wanted, live);

        Assert.Equal(2, script.Count);
        Assert.Contains("DROP CONSTRAINT \"CK_Doc_Qty\"", script[0]);
        Assert.Contains("ADD CONSTRAINT \"CK_Doc_Qty\" CHECK ((\"Qty\" >= 1))", script[1]);
        Assert.DoesNotContain(script, s => s.Contains("VALIDATE"));
    }

    [Fact]
    public void A_constraint_declared_not_valid_matches_a_live_one_that_is_not_valid()
    {
        var notValid = new PostgresConstraint("CK_Doc_Qty", """CHECK (("Qty" >= 0)) NOT VALID""");

        Assert.Empty(SnapshotComparer.Compare([Table([notValid])], [Table([notValid])]));
        Assert.Empty(Script(Table([notValid]), Table([notValid])));
    }

    // ---- inspect

    [Fact]
    public void Inspect_marks_an_invalid_index_instead_of_presenting_it_as_a_good_one()
    {
        var table = Table(indexes: [new PostgresIndex("IX_Doc_Name", IndexDefinition, IsValid: false)]);

        var sql = DesiredStateRenderer.Render([table])[0].Content;

        Assert.Contains("INVALID", sql);
        Assert.Contains(IndexDefinition, sql);
    }

    [Fact]
    public void Inspect_adds_no_marker_to_a_valid_index()
    {
        var table = Table(indexes: [new PostgresIndex("IX_Doc_Name", IndexDefinition)]);

        Assert.DoesNotContain("INVALID", DesiredStateRenderer.Render([table])[0].Content);
    }
}
