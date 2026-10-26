using Schemorph.Core.Operations;
using Schemorph.Core.Providers;

namespace Schemorph.Core.Tests.Operations;

/// <summary>
/// Whether a run enables destructive changes decides whether the provider is asked to keep a dropped
/// column out of its comparison. The plan <c>diff</c> shows and the comparison <c>apply</c> gates and
/// publishes must be asked the same question, or the plan a reviewer signed would not be the one that
/// runs.
/// </summary>
public sealed class WithholdColumnDropsRequestTests
{
    private const string Conn = "conn";

    private static FakeProvider Provider(FakeLedger ledger) => new()
    {
        Ledger = ledger,
        DesiredState = new FakeDesiredState(),
        Programmables = new ProgrammableAnalysis(Array.Empty<ProgrammableObjectInfo>(), Array.Empty<RawMessage>()),
        CompareOutcome = new CompareResult(Array.Empty<RawChange>(), Array.Empty<RawMessage>(), UpdateScript: null),
        ApplyOutcome = new ApplyResult(true, Array.Empty<RawChange>(), Array.Empty<RawChange>(), Array.Empty<RawMessage>()),
        Computed = new CompareResult(Array.Empty<RawChange>(), Array.Empty<RawMessage>(), UpdateScript: null),
    };

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Diff_asks_for_the_drops_to_be_kept_out_unless_destructive_changes_are_enabled(
        bool allowDestructive, bool expectedWithhold)
    {
        var ledger = new FakeLedger();
        var provider = Provider(ledger);

        await DiffOperation.RunAsync(provider, ledger, "schema", Conn, allowDestructive);

        Assert.Equal(expectedWithhold, Assert.Single(provider.CompareRequests).WithholdColumnDrops);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Apply_asks_the_same_question_diff_does(bool allowDestructive, bool expectedWithhold)
    {
        var ledger = new FakeLedger();
        var provider = Provider(ledger);

        await ApplyOperation.RunAsync(provider, ledger,
            new ApplyOperation.Request("schema", Conn, AllowDestructive: allowDestructive));

        Assert.Equal(expectedWithhold, Assert.Single(provider.ApplyRequests).WithholdColumnDrops);
    }
}
