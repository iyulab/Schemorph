using Schemorph.Core.Planning;

namespace Schemorph.Core.Tests.Planning;

/// <summary>
/// The review document a person signs. Its whole value is that the text reviewed
/// and the text executed are the same artifact, tied to the fingerprint the apply
/// gate enforces — so these tests pin verbatim inclusion, execution order, the
/// header's contents, and the refusal to emit a partial document. The executed
/// text now rides the plan itself (<see cref="Plan.UpdateScript"/>), the same
/// field the fingerprint binds.
/// </summary>
public sealed class ReviewScriptRendererTests
{
    private static readonly DateTimeOffset At = new(2026, 7, 21, 9, 30, 0, TimeSpan.Zero);

    private static PlanAction Declarative(string name, RiskLevel risk = RiskLevel.Safe) =>
        new(name, "Table", PlanOperation.Alter, risk, Sql: "-- attributed slice, NOT the artifact");

    private static PlanAction Redefine(string name, string sql) =>
        new(name, "View", PlanOperation.Redefine, RiskLevel.Safe, Sql: sql);

    private static Plan PlanOf(params PlanAction[] actions) =>
        new(Plan.CurrentFormatVersion, actions, Array.Empty<PlanMessage>());

    private static Plan PlanOf(string? updateScript, params PlanAction[] actions) =>
        new(Plan.CurrentFormatVersion, actions, Array.Empty<PlanMessage>(),
            Core.Providers.ApplyAtomicity.Partial, updateScript);

    [Fact]
    public void The_declarative_script_is_the_engines_own_text_not_a_reassembly()
    {
        var plan = PlanOf("ALTER TABLE dbo.Orders ADD Note NVARCHAR(50);",
            Declarative("dbo.Orders"), Declarative("dbo.Items"));

        var doc = ReviewScriptRenderer.Render(plan, "conn", At);

        Assert.Contains("ALTER TABLE dbo.Orders ADD Note NVARCHAR(50);", doc);
        // The per-change slices exist for explanation only. Reviewing a reassembly
        // of them is exactly the consumer workaround this feature replaces.
        Assert.DoesNotContain("attributed slice", doc);
    }

    [Fact]
    public void Stages_appear_in_execution_order_with_redefines_verbatim()
    {
        var plan = PlanOf("ALTER TABLE dbo.Orders ADD Note INT;",
            Declarative("dbo.Orders"),
            Redefine("dbo.VOrders", "CREATE OR ALTER VIEW dbo.VOrders AS SELECT 1 AS X;"),
            Redefine("dbo.VItems", "CREATE OR ALTER VIEW dbo.VItems AS SELECT 2 AS Y;"));

        var doc = ReviewScriptRenderer.Render(plan, "conn", At);

        Assert.Contains("CREATE OR ALTER VIEW dbo.VOrders AS SELECT 1 AS X;", doc);
        Assert.Contains("CREATE OR ALTER VIEW dbo.VItems AS SELECT 2 AS Y;", doc);

        // ADR-0002 order: declarative publish, then re-definitions — and the plan's
        // own dependency order within them.
        Assert.True(doc.IndexOf("Stage 1 of 2", StringComparison.Ordinal)
                  < doc.IndexOf("Stage 2 of 2", StringComparison.Ordinal));
        Assert.True(doc.IndexOf("ALTER TABLE dbo.Orders", StringComparison.Ordinal)
                  < doc.IndexOf("dbo.VOrders", StringComparison.Ordinal));
        Assert.True(doc.IndexOf("dbo.VOrders", StringComparison.Ordinal)
                  < doc.IndexOf("dbo.VItems", StringComparison.Ordinal));
    }

    [Fact]
    public void The_header_carries_the_hash_the_apply_gate_will_enforce()
    {
        var plan = PlanOf(Redefine("dbo.V", "CREATE OR ALTER VIEW dbo.V AS SELECT 1 AS X;"));

        var doc = ReviewScriptRenderer.Render(plan, "conn", At);
        var hash = PlanFingerprint.Compute(plan);

        // The paper a human signed and the fingerprint a machine enforces are one
        // artifact — that is the whole point of the header.
        Assert.Contains($"planHash:  {hash}", doc);
        Assert.Contains($"--expect-plan {hash}", doc);
        Assert.Contains("2026-07-21 09:30:00 UTC", doc);
        Assert.Contains("READ ONLY", doc);
    }

    [Fact]
    public void The_header_states_what_the_apply_guarantees()
    {
        // The signer must know the failure mode without knowing the engine
        // (ADR-0004 addendum): a partial apply and a transactional one leave
        // different databases behind the same failed command.
        var plan = PlanOf(Redefine("dbo.V", "CREATE OR ALTER VIEW dbo.V AS SELECT 1 AS X;"));

        Assert.Contains("atomicity: partial",
            ReviewScriptRenderer.Render(plan, "conn", At));
        Assert.Contains("atomicity: transactional",
            ReviewScriptRenderer.Render(
                plan with { Atomicity = Core.Providers.ApplyAtomicity.Transactional }, "conn", At));
    }

    [Fact]
    public void The_target_is_redacted()
    {
        var plan = PlanOf(Redefine("dbo.V", "CREATE OR ALTER VIEW dbo.V AS SELECT 1 AS X;"));

        var doc = ReviewScriptRenderer.Render(
            plan, "Server=db;Database=Prod;User Id=svc;Password=hunter2", At);

        Assert.DoesNotContain("hunter2", doc);
        Assert.Contains("Database=Prod", doc);   // still identifies what was reviewed
    }

    [Fact]
    public void Destructive_changes_are_marked_where_a_reviewer_reads()
    {
        var plan = PlanOf("DROP TABLE dbo.Legacy;", Declarative("dbo.Legacy", RiskLevel.Destructive));

        var doc = ReviewScriptRenderer.Render(plan, "conn", At);

        Assert.Contains("DESTRUCTIVE", doc);
    }

    [Fact]
    public void A_missing_update_script_fails_rather_than_emitting_a_partial_document()
    {
        // A document holding only the re-definitions would be signed for changes
        // nobody read — the exact failure mode this feature exists to remove.
        var plan = PlanOf(
            Declarative("dbo.Orders"),
            Redefine("dbo.V", "CREATE OR ALTER VIEW dbo.V AS SELECT 1 AS X;"));

        var ex = Assert.Throws<ReviewScriptRenderer.ScriptUnavailableException>(
            () => ReviewScriptRenderer.Render(plan, "conn", At));

        Assert.Contains("1 declarative change(s)", ex.Message);
    }

    [Fact]
    public void The_refusal_echoes_the_reported_diagnostic_rather_than_asserting_one()
    {
        // Which diagnostic explains a missing script is provider-specific — a code
        // one provider emits, another cannot. Naming a fixed one states a cause
        // that may never have fired and hides the one that did, so the refusal
        // quotes what the plan actually carries.
        var plan = new Plan(Plan.CurrentFormatVersion, [Declarative("dbo.Orders")],
            [new PlanMessage("Warning", "SCHEMORPH002", "Update-script generation failed.")]);

        var ex = Assert.Throws<ReviewScriptRenderer.ScriptUnavailableException>(
            () => ReviewScriptRenderer.Render(plan, "conn", At));

        Assert.Contains("SCHEMORPH002", ex.Message);
    }

    [Fact]
    public void The_refusal_says_so_when_no_diagnostic_explains_the_absence()
    {
        var plan = PlanOf(Declarative("dbo.Orders"));

        var ex = Assert.Throws<ReviewScriptRenderer.ScriptUnavailableException>(
            () => ReviewScriptRenderer.Render(plan, "conn", At));

        Assert.Contains("no diagnostic", ex.Message);
        Assert.DoesNotContain("SCHEMORPH", ex.Message);
    }

    [Fact]
    public void A_redefine_only_plan_needs_no_update_script()
    {
        // The absence is only dishonest when there is something it should have covered.
        var plan = PlanOf(Redefine("dbo.V", "CREATE OR ALTER VIEW dbo.V AS SELECT 1 AS X;"));

        var doc = ReviewScriptRenderer.Render(plan, "conn", At);

        Assert.Contains("Stage 1 of 2", doc);   // re-definitions are the only stage present
        Assert.Contains("CREATE OR ALTER VIEW dbo.V", doc);
    }

    [Fact]
    public void An_empty_plan_says_so_instead_of_rendering_an_empty_file()
    {
        var doc = ReviewScriptRenderer.Render(PlanOf(), "conn", At);

        Assert.Contains("No changes.", doc);
    }

    /// <summary>
    /// The document is the engine's text verbatim, so it can contain DDL for objects
    /// the plan never executes — a history-ledger DROP appears in every comparison
    /// against a target that already has a ledger. Reading a review document, a
    /// statement means "this runs"; where that is untrue the document has to say so
    /// itself, because the reader is signing the text and not the plan model.
    /// </summary>
    [Fact]
    public void Objects_present_in_the_script_but_not_executed_are_named_before_it()
    {
        var plan = PlanOf("ALTER TABLE dbo.Orders ADD Note INT;\nDROP TABLE dbo.__SchemorphHistory;",
            Declarative("dbo.Orders")) with
        {
            Excluded = [new PlanExclusion("dbo.__SchemorphHistory", "Schemorph's own history ledger.")],
        };

        var doc = ReviewScriptRenderer.Render(plan, "conn", At);

        Assert.Contains("NOT EXECUTED", doc);
        Assert.Contains("dbo.__SchemorphHistory", doc);
        Assert.Contains("Schemorph's own history ledger.", doc);

        // Ahead of the script: a caveat found after the statement is a caveat found
        // after the reviewer already read the statement as executable.
        Assert.True(doc.IndexOf("NOT EXECUTED", StringComparison.Ordinal)
                  < doc.IndexOf("DROP TABLE dbo.__SchemorphHistory;", StringComparison.Ordinal));

        // The far worse outcome than a false stop is teaching a reader that DROPs here
        // are inert. The notice bounds itself so the lesson stays "this one does not run".
        Assert.Contains("Anything NOT listed here does run", doc);
    }

    [Fact]
    public void A_plan_that_executes_everything_it_contains_carries_no_notice()
    {
        var plan = PlanOf("ALTER TABLE dbo.Orders ADD Note INT;", Declarative("dbo.Orders"));

        var doc = ReviewScriptRenderer.Render(plan, "conn", At);

        Assert.DoesNotContain("NOT EXECUTED", doc);
    }
}
