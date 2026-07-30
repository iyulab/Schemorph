using Schemorph.Core.Providers;

namespace Schemorph.Provider.Postgres.Shadow;

/// <summary>
/// Compares two single-schema snapshots — the shadow (desired state, applied)
/// against the live schema — into Schemorph's raw-change vocabulary. Pure:
/// both sides were already read in comparison mode
/// (<see cref="CatalogReader.ReadTablesAsync"/> with normalization), so every
/// text here is the engine's canonical rendering with same-schema references
/// unqualified, and equality is honest equality.
///
/// An index difference is a change to the table it is on, not a change of its
/// own. That is the first provider's shape — its plan folds an index addition
/// into the table's entry — and the contract is required to read the same on
/// both databases.
/// </summary>
internal static class SnapshotComparer
{
    public static IReadOnlyList<RawChange> Compare(
        IReadOnlyList<PgTable> desired, IReadOnlyList<PgTable> live)
    {
        var changes = new List<RawChange>();
        var liveByName = live.ToDictionary(t => t.Name, StringComparer.Ordinal);
        var desiredNames = desired.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);

        foreach (var want in desired)
        {
            if (!liveByName.TryGetValue(want.Name, out var have))
            {
                changes.Add(new RawChange("Add", "Table", want.Name));
                continue;
            }

            if (!ColumnsEqual(want, have) || !ConstraintsEqual(want, have) || !IndexesEqual(want, have))
            {
                changes.Add(new RawChange("Change", "Table", want.Name));
            }
        }

        foreach (var have in live)
        {
            if (!desiredNames.Contains(have.Name))
            {
                changes.Add(new RawChange("Delete", "Table", have.Name));
            }
        }

        return changes;
    }

    // Records compare by value, and no member's catalog order is part of a
    // table's identity: this comparison asks whether the two schemas mean the
    // same thing, and both sides are read in the engine's own order (columns by
    // attnum, constraints/indexes name-sorted), which is a reading artifact.
    // Sorting here makes the answer independent of it.

    // Column order specifically is NOT state. This is the project-wide policy —
    // "Schemorph diffs state, not ordinal position" — that the SQL Server
    // provider states as `IgnoreColumnOrder`; ordinal position is neither
    // declared in a model nor reachable by an ALTER. A column added to an
    // existing table lands last whatever the desired state says, so honoring
    // the difference plans a change no statement can carry out: the plan never
    // empties, and the apply has nothing to run. Where such a difference is
    // material (a positional `INSERT`, `SELECT *`), the remedy is naming the
    // columns, not rebuilding the table.
    private static bool ColumnsEqual(PgTable a, PgTable b)
        => a.Columns.OrderBy(c => c.Name, StringComparer.Ordinal)
            .SequenceEqual(b.Columns.OrderBy(c => c.Name, StringComparer.Ordinal));

    private static bool ConstraintsEqual(PgTable a, PgTable b)
        => a.Constraints.OrderBy(c => c.Name, StringComparer.Ordinal)
            .SequenceEqual(b.Constraints.OrderBy(c => c.Name, StringComparer.Ordinal));

    // The whole definition, exactly as constraints are compared: the reader has
    // already removed the one qualifier that differs between the two schemas, so
    // what remains distinguishes everything the engine distinguishes — sort
    // direction, NULLS placement, operator class, collation, INCLUDE columns and
    // predicate alike. A projection over columns cannot do that, and reporting
    // two such indexes as equal is drift the tool would never plan away.
    private static bool IndexesEqual(PgTable a, PgTable b)
        => a.Indexes.OrderBy(i => i.Name, StringComparer.Ordinal)
            .SequenceEqual(b.Indexes.OrderBy(i => i.Name, StringComparer.Ordinal));
}
