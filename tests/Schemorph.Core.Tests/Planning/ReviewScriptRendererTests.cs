using Schemorph.Core.Planning;

namespace Schemorph.Core.Tests.Planning;

/// <summary>
/// The review document a person signs. Its whole value is that the text reviewed
/// and the text executed are the same artifact, tied to the fingerprint the apply
/// gate enforces — so these tests pin verbatim inclusion, execution order, the
/// header's contents, and the refusal to emit a partial document.
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

    [Fact]
    public void The_declarative_script_is_the_engines_own_text_not_a_reassembly()
    {
        var plan = PlanOf(Declarative("dbo.Orders"), Declarative("dbo.Items"));

        var doc = ReviewScriptRenderer.Render(plan, "ALTER TABLE dbo.Orders ADD Note NVARCHAR(50);", "conn", At);

        Assert.Contains("ALTER TABLE dbo.Orders ADD Note NVARCHAR(50);", doc);
        // The per-change slices exist for explanation only. Reviewing a reassembly
        // of them is exactly the consumer workaround this feature replaces.
        Assert.DoesNotContain("attributed slice", doc);
    }

    [Fact]
    public void Stages_appear_in_execution_order_with_redefines_verbatim()
    {
        var plan = PlanOf(
            Declarative("dbo.Orders"),
            Redefine("dbo.VOrders", "CREATE OR ALTER VIEW dbo.VOrders AS SELECT 1 AS X;"),
            Redefine("dbo.VItems", "CREATE OR ALTER VIEW dbo.VItems AS SELECT 2 AS Y;"));

        var doc = ReviewScriptRenderer.Render(plan, "ALTER TABLE dbo.Orders ADD Note INT;", "conn", At);

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

        var doc = ReviewScriptRenderer.Render(plan, updateScript: null, "conn", At);
        var hash = PlanFingerprint.Compute(plan);

        // The paper a human signed and the fingerprint a machine enforces are one
        // artifact — that is the whole point of the header.
        Assert.Contains($"planHash:  {hash}", doc);
        Assert.Contains($"--expect-plan {hash}", doc);
        Assert.Contains("2026-07-21 09:30:00 UTC", doc);
        Assert.Contains("READ ONLY", doc);
    }

    [Fact]
    public void The_target_is_redacted()
    {
        var plan = PlanOf(Redefine("dbo.V", "CREATE OR ALTER VIEW dbo.V AS SELECT 1 AS X;"));

        var doc = ReviewScriptRenderer.Render(
            plan, null, "Server=db;Database=Prod;User Id=svc;Password=hunter2", At);

        Assert.DoesNotContain("hunter2", doc);
        Assert.Contains("Database=Prod", doc);   // still identifies what was reviewed
    }

    [Fact]
    public void Destructive_changes_are_marked_where_a_reviewer_reads()
    {
        var plan = PlanOf(Declarative("dbo.Legacy", RiskLevel.Destructive));

        var doc = ReviewScriptRenderer.Render(plan, "DROP TABLE dbo.Legacy;", "conn", At);

        Assert.Contains("DESTRUCTIVE", doc);
    }

    [Fact]
    public void A_missing_update_script_fails_rather_than_emitting_a_partial_document()
    {
        // SCHEMORPH002: the engine could not generate the declarative script. A
        // document holding only the re-definitions would be signed for changes
        // nobody read — the exact failure mode this feature exists to remove.
        var plan = PlanOf(
            Declarative("dbo.Orders"),
            Redefine("dbo.V", "CREATE OR ALTER VIEW dbo.V AS SELECT 1 AS X;"));

        var ex = Assert.Throws<ReviewScriptRenderer.ScriptUnavailableException>(
            () => ReviewScriptRenderer.Render(plan, updateScript: null, "conn", At));

        Assert.Contains("SCHEMORPH002", ex.Message);
    }

    [Fact]
    public void A_redefine_only_plan_needs_no_update_script()
    {
        // The absence is only dishonest when there is something it should have covered.
        var plan = PlanOf(Redefine("dbo.V", "CREATE OR ALTER VIEW dbo.V AS SELECT 1 AS X;"));

        var doc = ReviewScriptRenderer.Render(plan, updateScript: null, "conn", At);

        Assert.Contains("Stage 1 of 2", doc);   // re-definitions are the only stage present
        Assert.Contains("CREATE OR ALTER VIEW dbo.V", doc);
    }

    [Fact]
    public void An_empty_plan_says_so_instead_of_rendering_an_empty_file()
    {
        var doc = ReviewScriptRenderer.Render(PlanOf(), updateScript: null, "conn", At);

        Assert.Contains("No changes.", doc);
    }
}
