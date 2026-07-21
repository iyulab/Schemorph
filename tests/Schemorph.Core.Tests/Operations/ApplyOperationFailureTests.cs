using Schemorph.Core.Operations;
using Schemorph.Core.Providers;

namespace Schemorph.Core.Tests.Operations;

/// <summary>
/// What a failed apply says about itself. Apply runs three strategies in order
/// and does not roll back across them (ADR-0004), so a failure in a later stage
/// leaves earlier stages committed. Reporting that as a bare error is the one
/// answer that is wrong: a production consumer read exactly such a response,
/// concluded "partially applied" where nothing had been, and wrote it into a
/// runbook. These tests pin the stage and the committed counts on the outcome.
/// </summary>
public sealed class ApplyOperationFailureTests
{
    private const string Conn = "conn";

    private static ProgrammableObjectInfo Obj(string name) =>
        new(name, "View", $"{name}.sql", $"body of {name}",
            $"CREATE OR ALTER VIEW {name} -- body of {name}", Array.Empty<string>());

    private static RawChange Change(string name) => new("Alter", "Table", name);

    /// <summary>A publish that succeeds and commits the given changes.</summary>
    private static ApplyResult Published(params RawChange[] applied) =>
        new(true, applied, Array.Empty<RawChange>(), Array.Empty<RawMessage>());

    [Fact]
    public async Task Redefine_failure_names_its_stage_and_what_had_committed()
    {
        var ledger = new FakeLedger();
        var provider = new FakeProvider
        {
            Ledger = ledger,
            DesiredState = new FakeDesiredState(),
            // dbo.Second fails; dbo.First ran before it, dbo.Third never runs.
            Programmables = new ProgrammableAnalysis(
                new[] { Obj("dbo.First"), Obj("dbo.Second"), Obj("dbo.Third") },
                Array.Empty<RawMessage>()),
            ApplyOutcome = Published(Change("dbo.Orders"), Change("dbo.Items")),
            FailOnScriptContaining = "dbo.Second",
        };

        var outcome = await ApplyOperation.RunAsync(
            provider, ledger, new ApplyOperation.Request("schema", Conn));

        Assert.False(outcome.Success);
        Assert.Equal(ApplyOperation.FailureStage.Redefine, outcome.Stage);
        // The two declarative changes committed with the publish and are gone
        // from no envelope: the outcome still carries them.
        Assert.Equal(new[] { "dbo.Orders", "dbo.Items" }, outcome.Applied.Select(c => c.ObjectName));
        Assert.Equal(new[] { "dbo.First" }, outcome.Redefines!.Redefined);
        Assert.Contains("dbo.Second", outcome.Errors[0].Text);
        Assert.Equal("redefine_execution_failed", outcome.Errors[0].Code);
    }

    [Fact]
    public async Task Redefine_failure_is_recorded_in_the_ledger_as_a_failure_row()
    {
        var ledger = new FakeLedger();
        var provider = new FakeProvider
        {
            Ledger = ledger,
            DesiredState = new FakeDesiredState(),
            Programmables = new ProgrammableAnalysis(new[] { Obj("dbo.Boom") }, Array.Empty<RawMessage>()),
            ApplyOutcome = Published(),
            FailOnScriptContaining = "dbo.Boom",
        };

        await ApplyOperation.RunAsync(provider, ledger, new ApplyOperation.Request("schema", Conn));

        // The rendering change must not have cost the audit trail its failure row.
        var failure = Assert.Single(ledger.Entries, e => !e.Succeeded);
        Assert.Equal("dbo.Boom", failure.ObjectName);
    }

    [Fact]
    public async Task A_desired_state_error_still_reports_nothing_committed()
    {
        var ledger = new FakeLedger();
        var provider = new FakeProvider
        {
            Ledger = ledger,
            DesiredState = new FakeDesiredState(
                ErrorList: new[] { new RawMessage("Error", "SCHEMORPH007", "bad.sql: parse error") }),
        };

        var outcome = await ApplyOperation.RunAsync(
            provider, ledger, new ApplyOperation.Request("schema", Conn));

        Assert.Equal(ApplyOperation.FailureStage.DesiredState, outcome.Stage);
        Assert.Empty(outcome.Applied);
        Assert.Null(outcome.Redefines);
    }
}
