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
        var result = SnapshotComparer.Compare([Table("A")], [Table("A")]);

        Assert.Empty(result.Changes);
        Assert.Empty(result.OutOfScope);
    }

    [Fact]
    public void A_missing_table_is_an_add_and_an_extra_one_a_delete()
    {
        var result = SnapshotComparer.Compare([Table("New")], [Table("Old")]);

        Assert.Equal(2, result.Changes.Count);
        Assert.Contains(new RawChange("Add", "Table", "New"), result.Changes);
        Assert.Contains(new RawChange("Delete", "Table", "Old"), result.Changes);
    }

    [Fact]
    public void A_column_difference_is_a_table_change()
    {
        var want = Table("A", new PgColumn("Id", "uuid", true, null), new PgColumn("Note", "text", false, null));
        var have = Table("A", new PgColumn("Id", "uuid", true, null));

        var change = Assert.Single(SnapshotComparer.Compare([want], [have]).Changes);
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

        Assert.Empty(SnapshotComparer.Compare([want], [same]).Changes);
        Assert.Single(SnapshotComparer.Compare([want], [different]).Changes);
    }

    [Fact]
    public void An_index_difference_is_out_of_scope_not_a_silent_pass()
    {
        // Index planning is slice P2. The provider must refuse, not emit a plan
        // that cannot see the difference (§2: the backstop pins the manifest).
        var want = Table("A") with { Indexes = [new PgIndex("IX", "CREATE INDEX \"IX\" ON \"A\" (\"x\")")] };
        var have = Table("A");

        var result = SnapshotComparer.Compare([want], [have]);

        Assert.Empty(result.Changes);
        var reason = Assert.Single(result.OutOfScope);
        Assert.Contains("A", reason);
    }
}
