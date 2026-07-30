using Schemorph.Core.Providers;
using Schemorph.Provider.Postgres.Shadow;

namespace Schemorph.Provider.Postgres.Tests;

public class SnapshotComparerTests
{
    private static PgTable Table(string name, params PgColumn[] columns) => new(
        "s", name,
        columns.Length == 0 ? [new PgColumn("Id", "uuid", true, null)] : columns,
        [], []);

    [Fact]
    public void Identical_snapshots_compare_empty()
    {
        Assert.Empty(SnapshotComparer.Compare([Table("A")], [Table("A")]));
    }

    [Fact]
    public void A_missing_table_is_an_add_and_an_extra_one_a_delete()
    {
        var result = SnapshotComparer.Compare([Table("New")], [Table("Old")]);

        Assert.Equal(2, result.Count);
        Assert.Contains(new RawChange("Add", "Table", "New"), result);
        Assert.Contains(new RawChange("Delete", "Table", "Old"), result);
    }

    [Fact]
    public void A_column_difference_is_a_table_change()
    {
        var want = Table("A", new PgColumn("Id", "uuid", true, null), new PgColumn("Note", "text", false, null));
        var have = Table("A", new PgColumn("Id", "uuid", true, null));

        var change = Assert.Single(SnapshotComparer.Compare([want], [have]));
        Assert.Equal(new RawChange("Change", "Table", "A"), change);
    }

    [Fact]
    public void Columns_that_differ_only_in_position_are_not_a_change()
    {
        // A column added to an existing table lands last no matter where the
        // desired state declares it, and no ALTER moves it. Reading the
        // difference as a change plans work no statement can carry out — the
        // plan never empties and the apply has nothing to run.
        var id = new PgColumn("Id", "uuid", true, null);
        var note = new PgColumn("Note", "text", false, null);
        var stamp = new PgColumn("Stamp", "timestamptz", true, "now()");

        var declared = Table("A", id, note, stamp);
        var live = Table("A", id, stamp, note);

        Assert.Empty(SnapshotComparer.Compare([declared], [live]));
    }

    [Fact]
    public void A_reordered_column_whose_definition_also_changed_is_still_a_change()
    {
        // The order-insensitive comparison must not become a blind one: the
        // reordered column carries a real definition difference here.
        var id = new PgColumn("Id", "uuid", true, null);
        var declared = Table("A", id, new PgColumn("Note", "text", true, null));
        var live = Table("A", new PgColumn("Note", "text", false, null), id);

        var change = Assert.Single(SnapshotComparer.Compare([declared], [live]));
        Assert.Equal(new RawChange("Change", "Table", "A"), change);
    }

    [Fact]
    public void A_constraint_difference_is_a_table_change_regardless_of_order()
    {
        var pk = new PgConstraint("PK_A", "PRIMARY KEY (\"Id\")");
        var ck = new PgConstraint("CK_A", "CHECK ((\"x\" > 0))");
        var want = Table("A") with { Constraints = [pk, ck] };
        var same = Table("A") with { Constraints = [ck, pk] };
        var different = Table("A") with { Constraints = [pk] };

        Assert.Empty(SnapshotComparer.Compare([want], [same]));
        Assert.Single(SnapshotComparer.Compare([want], [different]));
    }

    [Fact]
    public void An_index_difference_is_a_change_to_the_table_it_is_on()
    {
        // Parity with the first provider, whose plan folds an index addition
        // into the table's own entry rather than giving it a separate one.
        var want = Table("A") with { Indexes = [new PgIndex("IX", """CREATE INDEX "IX" ON "A" ("x")""")] };
        var have = Table("A");

        var change = Assert.Single(SnapshotComparer.Compare([want], [have]));
        Assert.Equal(new RawChange("Change", "Table", "A"), change);
    }

    [Fact]
    public void Two_indexes_of_the_same_name_that_read_differently_are_a_change()
    {
        // The whole definition is the identity. A comparison over column names
        // alone calls these equal, and then no plan ever removes the drift —
        // which is why the reader keeps the engine's text instead of a projection.
        var want = Table("A") with
        {
            Indexes = [new PgIndex("IX", """CREATE INDEX "IX" ON "A" USING btree ("x" DESC)""")],
        };
        var have = Table("A") with
        {
            Indexes = [new PgIndex("IX", """CREATE INDEX "IX" ON "A" USING btree ("x")""")],
        };

        Assert.Single(SnapshotComparer.Compare([want], [have]));
    }

    [Fact]
    public void Indexes_that_differ_only_in_read_order_are_not_a_change()
    {
        var a = new PgIndex("IX_A", """CREATE INDEX "IX_A" ON "A" ("x")""");
        var b = new PgIndex("IX_B", """CREATE INDEX "IX_B" ON "A" ("y")""");

        var want = Table("A") with { Indexes = [a, b] };
        var same = Table("A") with { Indexes = [b, a] };

        Assert.Empty(SnapshotComparer.Compare([want], [same]));
    }
}
