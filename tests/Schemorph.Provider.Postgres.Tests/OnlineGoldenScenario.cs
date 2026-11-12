using Schemorph.Provider.Postgres.Shadow;

namespace Schemorph.Provider.Postgres.Tests;

/// <summary>
/// A change that touches every statement class the synthesizer orders - constraint drops and adds (CHECK,
/// FOREIGN KEY, PRIMARY KEY, a validation still owed), index drops, rebuilds and creates (plain, unique,
/// invalid), a created table with its own constraint and index, and a dropped table. Used to pin the
/// synthesizer's default output: <see cref="PostgresOnlineSynthesisTests"/> compares it to the list the code
/// produced before the online stage existed.
/// </summary>
internal static class OnlineGoldenScenario
{
    private const string Check = """CHECK (("Qty" >= 0))""";
    private const string ForeignKey = """FOREIGN KEY ("OtherId") REFERENCES "Other" ("Id")""";

    private static string Index(string name, string column = "Name", bool unique = false, string table = "Doc")
        => $"""CREATE {(unique ? "UNIQUE " : "")}INDEX "{name}" ON "{table}" USING btree ("{column}")""";

    private static PostgresTable Table(string name, IReadOnlyList<PostgresConstraint> constraints, IReadOnlyList<PostgresIndex> indexes) => new(
        "s", name,
        [new PostgresColumn("Id", "integer", true, null), new PostgresColumn("Qty", "integer", false, null)],
        constraints, indexes);

    public static (IReadOnlyList<PostgresTable> Desired, IReadOnlyList<PostgresTable> Live) Build()
    {
        var live = new[]
        {
            Table("Doc",
                [new PostgresConstraint("CK_old", Check), new PostgresConstraint("CK_wait", Check + " NOT VALID")],
                [new PostgresIndex("IX_Doc_Name", Index("IX_Doc_Name")), new PostgresIndex("IX_gone", Index("IX_gone")),
                 new PostgresIndex("IX_bad", Index("IX_bad"), IsValid: false)]),
            Table("Retired", [], []),
        };
        var desired = new[]
        {
            Table("Doc",
                [new PostgresConstraint("CK_wait", Check), new PostgresConstraint("FK_new", ForeignKey),
                 new PostgresConstraint("CK_new", """CHECK (("Qty" > 1))"""), new PostgresConstraint("PK_Doc", """PRIMARY KEY ("Id")""")],
                [new PostgresIndex("IX_Doc_Name", Index("IX_Doc_Name", "Qty")), new PostgresIndex("IX_bad", Index("IX_bad")),
                 new PostgresIndex("UX_new", Index("UX_new", unique: true))]),
            Table("Fresh",
                [new PostgresConstraint("CK_f", Check)],
                [new PostgresIndex("IX_f", Index("IX_f", table: "Fresh"))]),
        };
        return (desired, live);
    }
}
