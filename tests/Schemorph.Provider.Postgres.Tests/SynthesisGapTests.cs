using Schemorph.Core.Providers;
using Schemorph.Provider.Postgres.Shadow;

namespace Schemorph.Provider.Postgres.Tests;

/// <summary>
/// The provider checks its own work before reporting any of it as done. Comparison
/// and synthesis reach their answers independently — one over structural equality,
/// the other over per-member differences — so they can disagree, and the failure
/// mode of an undetected disagreement is the worst kind: the apply executes
/// nothing, reports the change as applied, and writes a success row into the audit
/// trail, after which the next diff reports the same change again, forever.
///
/// The disagreement is not reachable through the current comparison (every field
/// the comparison judges has a synthesis branch), which is exactly why the guard
/// is pinned here rather than through a live loop: what protects a future slice
/// has to be tested on its own terms.
/// </summary>
public class SynthesisGapTests
{
    private static DdlSynthesizer.Statement Statement(string objectName) =>
        new(objectName, $"ALTER TABLE \"{objectName}\" ADD COLUMN x integer;");

    [Fact]
    public void No_changes_is_not_a_gap()
    {
        Assert.Null(PostgresProvider.SynthesisGap([], []));
        Assert.Null(PostgresProvider.SynthesisGap([], [Statement("A")]));
    }

    [Fact]
    public void A_change_carried_by_a_statement_is_not_a_gap()
    {
        var changes = new[] { new RawChange("Change", "Table", "A"), new RawChange("Add", "Table", "B") };

        Assert.Null(PostgresProvider.SynthesisGap(changes, [Statement("A"), Statement("B")]));
    }

    [Fact]
    public void A_change_no_statement_carries_is_an_error_that_names_it()
    {
        var changes = new[] { new RawChange("Change", "Table", "A") };

        var gap = PostgresProvider.SynthesisGap(changes, []);

        Assert.NotNull(gap);
        Assert.Equal("Error", gap.Severity);
        Assert.Equal("SCHEMORPH009", gap.Code);
        Assert.Contains("Change Table A", gap.Text);
    }

    [Fact]
    public void A_gap_beside_a_carried_change_is_still_a_gap()
    {
        // The count-based shortcut this replaces — "changes exist but no statements
        // do" — sees nothing here, because one change did synthesize.
        var changes = new[] { new RawChange("Change", "Table", "A"), new RawChange("Change", "Table", "B") };

        var gap = PostgresProvider.SynthesisGap(changes, [Statement("A")]);

        Assert.NotNull(gap);
        Assert.Contains("Change Table B", gap.Text);
        Assert.DoesNotContain("Table A", gap.Text);
    }
}
