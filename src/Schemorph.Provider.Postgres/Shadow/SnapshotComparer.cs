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
/// Slice discipline (§2 of the dev plan): this slice compares tables, columns
/// and constraints. An INDEX difference is real work the provider cannot plan
/// yet (P2), so it is reported as out of scope for the caller to refuse on —
/// silently ignoring it would emit a plan that claims a sync it cannot see.
/// </summary>
internal static class SnapshotComparer
{
    public sealed record Comparison(
        IReadOnlyList<RawChange> Changes,
        IReadOnlyList<string> OutOfScope);

    public static Comparison Compare(
        IReadOnlyList<PgTable> desired, IReadOnlyList<PgTable> live)
    {
        var changes = new List<RawChange>();
        var outOfScope = new List<string>();
        var liveByName = live.ToDictionary(t => t.Name, StringComparer.Ordinal);
        var desiredNames = desired.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);

        foreach (var want in desired)
        {
            if (!liveByName.TryGetValue(want.Name, out var have))
            {
                changes.Add(new RawChange("Add", "Table", want.Name));
                continue;
            }

            if (!ColumnsEqual(want, have) || !ConstraintsEqual(want, have))
            {
                changes.Add(new RawChange("Change", "Table", want.Name));
            }

            if (!IndexesEqual(want, have))
            {
                outOfScope.Add($"index change on table {want.Name}");
            }
        }

        foreach (var have in live)
        {
            if (!desiredNames.Contains(have.Name))
            {
                changes.Add(new RawChange("Delete", "Table", have.Name));
            }
        }

        return new Comparison(changes, outOfScope);
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

    // NOT the CreateStatement: pg_get_indexdef(oid) always qualifies the table,
    // so full-text comparison across two schemas never converges. The identity
    // is the structural projection — the per-column renderings are the engine's
    // own text and carry no qualifier.
    private static bool IndexesEqual(PgTable a, PgTable b)
        => IndexIdentities(a).SequenceEqual(IndexIdentities(b));

    private static IEnumerable<string> IndexIdentities(PgTable table)
        => table.Indexes
            .OrderBy(i => i.Name, StringComparer.Ordinal)
            .Select(i => string.Join("|",
                i.Name, i.Unique, i.Method, i.KeyCount, i.Predicate,
                string.Join(",", i.Keys ?? [])));
}
