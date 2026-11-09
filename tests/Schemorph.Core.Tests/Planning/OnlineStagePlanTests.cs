using Schemorph.Core.Operations;
using Schemorph.Core.Planning;
using Schemorph.Core.Providers;

namespace Schemorph.Core.Tests.Planning;

/// <summary>
/// The online stage (ADR-0009) as the plan carries it, judged without a database: the statements ride the
/// plan and its fingerprint, the renderers say which of them are not part of the atomic apply, a plan that
/// does not ask for the stage is what it always was, and a provider that cannot plan it is refused.
/// What the PostgreSQL provider puts in the list is in its own tests.
/// </summary>
public class OnlineStagePlanTests
{
    private static readonly OnlineStep Build =
        new("orders", "CREATE INDEX CONCURRENTLY \"IX_orders_date\" ON \"orders\" USING btree (\"date\");");
    private static readonly OnlineStep Validate =
        new("orders", "ALTER TABLE \"s\".\"orders\" VALIDATE CONSTRAINT \"CK_qty\";");

    private static PlanAction Action(string sql = "ALTER TABLE x;")
        => new("orders", "Table", PlanOperation.Alter, RiskLevel.Safe, sql, StatementCount: 1);

    private static Plan PlanWith(IReadOnlyList<OnlineStep>? online = null, string? updateScript = "SET LOCAL x;")
        => new(Plan.CurrentFormatVersion, new[] { Action() }, Array.Empty<PlanMessage>(),
            ApplyAtomicity.Transactional, updateScript, Online: online);

    // ---- fingerprint

    [Fact]
    public void A_plan_without_online_steps_hashes_as_the_identity_it_always_had()
    {
        // The pre-stage formula, restated here on purpose: if the online stage ever leaks into a plan that
        // does not ask for it, a hash reviewed before this stage existed stops passing the gate.
        const char field = '\u001F';
        const char record = '\u001E';
        var action = Action();
        var shape = string.Join(field, action.ObjectName, action.ObjectType, action.Operation.ToString(),
            action.Risk.ToString(), action.Sql);
        var expected = ContentChecksum.Compute($"{shape}{record}SET LOCAL x;");

        Assert.Equal(expected, PlanFingerprint.Compute(PlanWith()));
        Assert.Equal(expected, PlanFingerprint.Compute(PlanWith(Array.Empty<OnlineStep>())));
    }

    [Fact]
    public void The_online_statements_are_part_of_what_the_hash_binds()
    {
        var without = PlanFingerprint.Compute(PlanWith());
        var one = PlanFingerprint.Compute(PlanWith([Build]));
        var two = PlanFingerprint.Compute(PlanWith([Build, Validate]));

        Assert.NotEqual(without, one);
        Assert.NotEqual(one, two);
    }

    [Fact]
    public void The_order_of_the_online_statements_is_part_of_the_hash()
    {
        Assert.NotEqual(
            PlanFingerprint.Compute(PlanWith([Build, Validate])),
            PlanFingerprint.Compute(PlanWith([Validate, Build])));
    }

    [Fact]
    public void A_different_statement_for_the_same_object_is_a_different_plan()
    {
        var other = Build with { Sql = Build.Sql.Replace("date", "total") };

        Assert.NotEqual(PlanFingerprint.Compute(PlanWith([Build])), PlanFingerprint.Compute(PlanWith([other])));
    }

    // ---- JSON and text

    [Fact]
    public void The_json_lists_the_online_statements_beside_the_changes()
    {
        using var doc = System.Text.Json.JsonDocument.Parse(PlanRenderer.ToJson(PlanWith([Build, Validate])));

        var online = doc.RootElement.GetProperty("online");
        Assert.Equal(2, online.GetArrayLength());
        Assert.Equal("orders", online[0].GetProperty("objectName").GetString());
        Assert.Equal(Build.Sql, online[0].GetProperty("sql").GetString());
        Assert.Equal(Validate.Sql, online[1].GetProperty("sql").GetString());
        Assert.Equal("1.12", doc.RootElement.GetProperty("formatVersion").GetString());
    }

    [Fact]
    public void The_json_has_an_empty_online_list_when_the_stage_was_not_requested()
    {
        using var doc = System.Text.Json.JsonDocument.Parse(PlanRenderer.ToJson(PlanWith()));

        Assert.Equal(0, doc.RootElement.GetProperty("online").GetArrayLength());
    }

    [Fact]
    public void The_text_says_the_online_statements_are_not_part_of_the_atomic_apply()
    {
        var text = PlanRenderer.ToText(PlanWith([Build]));

        Assert.Contains("Online stage: 1 statement(s)", text);
        Assert.Contains("not part of the atomic apply", text);
        Assert.Contains(Build.Sql, text);
        Assert.DoesNotContain("Online stage", PlanRenderer.ToText(PlanWith()));
    }

    // ---- review script

    [Fact]
    public void The_review_script_without_an_online_stage_numbers_its_stages_as_it_always_did()
    {
        var script = ReviewScriptRenderer.Render(PlanWith(), "target", DateTimeOffset.UnixEpoch);

        Assert.Contains("Stage 1 of 2: declarative publish", script);
        Assert.DoesNotContain("online", script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_review_script_puts_the_online_stage_last_and_says_it_is_not_atomic()
    {
        var script = ReviewScriptRenderer.Render(PlanWith([Build, Validate]), "target", DateTimeOffset.UnixEpoch);

        Assert.Contains("Stage 1 of 2: declarative publish", script);
        Assert.Contains("Stage 2 of 2: online (2 statement(s))", script);
        Assert.Contains("NOT atomic", script);
        Assert.Contains(Build.Sql, script);
        Assert.True(script.IndexOf("SET LOCAL x;", StringComparison.Ordinal)
                    < script.IndexOf(Build.Sql, StringComparison.Ordinal),
            "the online statements come after the transactional script");
        Assert.True(script.IndexOf(Build.Sql, StringComparison.Ordinal)
                    < script.IndexOf(Validate.Sql, StringComparison.Ordinal));
    }

    [Fact]
    public void A_change_whose_every_statement_is_online_still_has_a_review_script()
    {
        // No transactional statement at all: the plan has no update script, and that is not "unavailable".
        var script = ReviewScriptRenderer.Render(PlanWith([Build], updateScript: null), "target", DateTimeOffset.UnixEpoch);

        Assert.Contains("every statement of these changes is in the online stage", script);
        Assert.Contains(Build.Sql, script);
    }

    [Fact]
    public void Without_a_script_and_without_online_statements_the_review_script_is_still_refused()
    {
        Assert.Throws<ReviewScriptRenderer.ScriptUnavailableException>(
            () => ReviewScriptRenderer.Render(PlanWith(updateScript: null), "target", DateTimeOffset.UnixEpoch));
    }

    // ---- plan builder

    [Fact]
    public void The_builder_carries_the_online_statements_into_the_plan()
    {
        var compared = new CompareResult(
            [new RawChange("Change", "Table", "orders")], Array.Empty<RawMessage>(), "SET LOCAL x;",
            [new ChangeScript("orders", "ALTER TABLE x;\nCREATE INDEX CONCURRENTLY y;", Rebuild: false, StatementCount: 2)],
            OnlineSteps: [Build, Validate]);

        var plan = PlanBuilder.Build(compared, allowDestructive: false, atomicity: ApplyAtomicity.Transactional);

        Assert.Equal([Build, Validate], plan.Online);
    }

    [Fact]
    public void A_statement_of_a_change_the_plan_holds_out_is_held_out_with_it()
    {
        var compared = new CompareResult(
            [new RawChange("Delete", "Table", "orders")], Array.Empty<RawMessage>(), "SET LOCAL x;",
            OnlineSteps: [Build]);

        var plan = PlanBuilder.Build(compared, allowDestructive: false, atomicity: ApplyAtomicity.Transactional);

        Assert.Empty(plan.Actions);
        Assert.Empty(plan.Online);
    }

    // ---- refusal

    [Fact]
    public async Task A_provider_that_cannot_plan_the_online_stage_refuses_a_diff_that_asks_for_it()
    {
        var provider = new FakeProvider();   // SupportsOnlineStage is false

        var ex = await Assert.ThrowsAsync<UnsupportedByProviderException>(
            () => DiffOperation.RunAsync(provider, new FakeLedger(), "schema-dir-that-is-never-read", "unused",
                allowDestructive: false, online: true));

        Assert.Equal("not_implemented", ex.ToError().Code);
        Assert.Contains("online stage", ex.Message);
    }
}
