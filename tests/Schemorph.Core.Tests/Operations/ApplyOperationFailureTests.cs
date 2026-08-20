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

    [Fact]
    public async Task A_transactional_providers_redefine_failure_rolls_back_the_session_and_still_records_a_failure_row()
    {
        var ledger = new FakeLedger();
        var session = new FakeApplySession();
        var provider = new FakeProvider
        {
            Ledger = ledger,
            Session = session,
            Capabilities = new(new[] { "fake" }, ApplyAtomicity.Transactional),
            DesiredState = new FakeDesiredState(),
            Programmables = new ProgrammableAnalysis(
                new[] { Obj("dbo.Boom") }, Array.Empty<RawMessage>()),
            ApplyOutcome = Published(Change("dbo.Orders")),
            FailOnScriptContaining = "dbo.Boom",
        };

        var outcome = await ApplyOperation.RunAsync(
            provider, ledger, new ApplyOperation.Request("schema", Conn));

        Assert.False(outcome.Success);
        Assert.Equal(ApplyOperation.FailureStage.Redefine, outcome.Stage);

        Assert.True(session.RolledBack);
        Assert.False(session.Committed);
        Assert.True(session.Disposed);

        // Every session-scoped call (ApplyAsync + the declarative ledger
        // append) actually got the session, and the redefine failure row is
        // still recorded via the failure path, which never sees a session.
        Assert.All(provider.SessionsSeen, s => Assert.Same(session, s));
        Assert.Contains(session, ledger.AppendSessionsSeen);
        var failure = Assert.Single(ledger.Entries, e => !e.Succeeded);
        Assert.Equal("dbo.Boom", failure.ObjectName);
    }

    [Fact]
    public async Task A_transactional_providers_ledger_bootstrap_never_joins_the_session()
    {
        var ledger = new FakeLedger();
        var session = new FakeApplySession();
        var provider = new FakeProvider
        {
            Ledger = ledger,
            Session = session,
            Capabilities = new(new[] { "fake" }, ApplyAtomicity.Transactional),
            DesiredState = new FakeDesiredState(),
            Programmables = new ProgrammableAnalysis(Array.Empty<ProgrammableObjectInfo>(), Array.Empty<RawMessage>()),
            ApplyOutcome = Published(Change("dbo.Orders")),
        };

        await ApplyOperation.RunAsync(provider, ledger, new ApplyOperation.Request("schema", Conn));

        // EnsureInitializedAsync is the ledger TABLE'S bootstrap — always
        // outside the session (spec addendum), even when one is open.
        Assert.Contains(null, ledger.EnsureInitializedSessionsSeen);
        Assert.DoesNotContain(session, ledger.EnsureInitializedSessionsSeen);
    }

    [Fact]
    public async Task A_transactional_providers_successful_apply_commits_the_session_once()
    {
        var ledger = new FakeLedger();
        var session = new FakeApplySession();
        var provider = new FakeProvider
        {
            Ledger = ledger,
            Session = session,
            Capabilities = new(new[] { "fake" }, ApplyAtomicity.Transactional),
            DesiredState = new FakeDesiredState(),
            Programmables = new ProgrammableAnalysis(Array.Empty<ProgrammableObjectInfo>(), Array.Empty<RawMessage>()),
            ApplyOutcome = Published(Change("dbo.Orders")),
        };

        var outcome = await ApplyOperation.RunAsync(provider, ledger, new ApplyOperation.Request("schema", Conn));

        Assert.True(outcome.Success);
        Assert.True(session.Committed);
        Assert.False(session.RolledBack);
        Assert.True(session.Disposed);
    }

    [Fact]
    public async Task A_transactional_providers_commit_failure_is_reported_not_thrown()
    {
        var ledger = new FakeLedger();
        var session = new FakeApplySession { CommitThrows = true };
        var provider = new FakeProvider
        {
            Ledger = ledger,
            Session = session,
            Capabilities = new(new[] { "fake" }, ApplyAtomicity.Transactional),
            DesiredState = new FakeDesiredState(),
            Programmables = new ProgrammableAnalysis(Array.Empty<ProgrammableObjectInfo>(), Array.Empty<RawMessage>()),
            ApplyOutcome = Published(Change("dbo.Orders")),
        };

        // Every stage before commit ran clean — a throw from CommitAsync must
        // still come back as a structured Outcome, not an unhandled exception
        // propagating out of RunAsync.
        var outcome = await ApplyOperation.RunAsync(
            provider, ledger, new ApplyOperation.Request("schema", Conn));

        Assert.False(outcome.Success);
        Assert.Equal(ApplyOperation.FailureStage.Commit, outcome.Stage);
        Assert.Equal("commit_failed", outcome.Errors[0].Code);
        Assert.Contains("connection lost", outcome.Errors[0].Text);
        Assert.Equal(new[] { "dbo.Orders" }, outcome.Applied.Select(c => c.ObjectName));

        // Best-effort rollback was attempted on the same session and succeeded.
        Assert.True(session.RolledBack);
        Assert.False(session.Committed);
        Assert.True(session.Disposed);
    }

    [Fact]
    public async Task A_partial_providers_apply_never_opens_a_session()
    {
        var ledger = new FakeLedger();
        var provider = new FakeProvider
        {
            Ledger = ledger,
            Session = new FakeApplySession(),   // set, but Capabilities stays Partial — must never be handed out
            DesiredState = new FakeDesiredState(),
            Programmables = new ProgrammableAnalysis(Array.Empty<ProgrammableObjectInfo>(), Array.Empty<RawMessage>()),
            ApplyOutcome = Published(Change("dbo.Orders")),
        };

        await ApplyOperation.RunAsync(provider, ledger, new ApplyOperation.Request("schema", Conn));

        Assert.All(provider.SessionsSeen, Assert.Null);
    }
}
