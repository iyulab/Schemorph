using Schemorph.Provider.Postgres.Shadow;

namespace Schemorph.Provider.Postgres.Tests;

/// <summary>
/// A change that touches every statement class the synthesizer orders - constraint drops and adds (CHECK,
/// FOREIGN KEY, PRIMARY KEY, a validation still owed), index drops, rebuilds and creates (plain, unique,
/// invalid), a created table with its own constraint and index, and a dropped table. Used to pin the
/// synthesizer's default output: <see cref="PgOnlineSynthesisTests"/> compares it to the list the code
/// produced before the online stage existed.
/// </summary>
internal static class OnlineGoldenScenario
{
    private const string Check = """CHECK (("Qty" >= 0))""";
    private const string ForeignKey = """FOREIGN KEY ("OtherId") REFERENCES "Other" ("Id")""";

    private static string Index(string name, string column = "Name", bool unique = false, string table = "Doc")
        => $"""CREATE {(unique ? "UNIQUE " : "")}INDEX "{name}" ON "{table}" USING btree ("{column}")""";

    private static PgTable Table(string name, IReadOnlyList<PgConstraint> constraints, IReadOnlyList<PgIndex> indexes) => new(
        "s", name,
        [new PgColumn("Id", "integer", true, null), new PgColumn("Qty", "integer", false, null)],
        constraints, indexes);

    public static (IReadOnlyList<PgTable> Desired, IReadOnlyList<PgTable> Live) Build()
    {
        var live = new[]
        {
            Table("Doc",
                [new PgConstraint("CK_old", Check), new PgConstraint("CK_wait", Check + " NOT VALID")],
                [new PgIndex("IX_Doc_Name", Index("IX_Doc_Name")), new PgIndex("IX_gone", Index("IX_gone")),
                 new PgIndex("IX_bad", Index("IX_bad"), IsValid: false)]),
            Table("Retired", [], []),
        };
        var desired = new[]
        {
            Table("Doc",
                [new PgConstraint("CK_wait", Check), new PgConstraint("FK_new", ForeignKey),
                 new PgConstraint("CK_new", """CHECK (("Qty" > 1))"""), new PgConstraint("PK_Doc", """PRIMARY KEY ("Id")""")],
                [new PgIndex("IX_Doc_Name", Index("IX_Doc_Name", "Qty")), new PgIndex("IX_bad", Index("IX_bad")),
                 new PgIndex("UX_new", Index("UX_new", unique: true))]),
            Table("Fresh",
                [new PgConstraint("CK_f", Check)],
                [new PgIndex("IX_f", Index("IX_f", table: "Fresh"))]),
        };
        return (desired, live);
    }
}
