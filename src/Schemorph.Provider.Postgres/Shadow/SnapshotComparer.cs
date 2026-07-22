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

    // Records compare by value; order matters on columns (attnum order is the
    // table's own order) but NOT on constraints/indexes, which the reader
    // returns name-sorted already — sorting again here keeps the comparison
    // independent of that implementation detail.

    private static bool ColumnsEqual(PgTable a, PgTable b)
        => a.Columns.SequenceEqual(b.Columns);

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
