using Schemorph.Core.Planning;

namespace Schemorph.Core.Tests.Planning;

public class PlanRendererTests
{
    private static readonly Plan Sample = new(
        Plan.CurrentFormatVersion,
        new[]
        {
            new PlanAction("dbo.Orders", "Table", PlanOperation.Create, RiskLevel.Safe),
            new PlanAction("dbo.LegacyLog", "Table", PlanOperation.Drop, RiskLevel.Destructive),
        },
        new[] { new PlanMessage("Warning", "SQL72015", "data loss could occur") });

    [Fact]
    public void Json_carries_version_flags_changes_and_messages()
    {
        var json = PlanRenderer.ToJson(Sample);

        Assert.Contains("\"formatVersion\": \"1.4\"", json);
        Assert.Contains("\"hasChanges\": true", json);
        Assert.Contains("\"hasDestructiveChanges\": true", json);
        Assert.Contains("\"changes\"", json);
        Assert.Contains("\"risk\": \"destructive\"", json);
        Assert.Contains("\"code\": \"SQL72015\"", json);
    }

    [Fact]
    public void Json_changes_carry_an_action_list_not_a_single_operation()
    {
        // Format 1.0 (docs/plan-format.md): per-change `actions` is a LIST so a
        // future composite operation (rebuild = drop + create) is not a breaking
        // change. Today every list holds exactly one verb.
        var json = PlanRenderer.ToJson(Sample);

        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var changes = doc.RootElement.GetProperty("changes");
        Assert.Equal(2, changes.GetArrayLength());
        var first = changes[0];
        Assert.Equal("dbo.Orders", first.GetProperty("objectName").GetString());
        var actions = first.GetProperty("actions");
        Assert.Equal(System.Text.Json.JsonValueKind.Array, actions.ValueKind);
        Assert.Equal("create", actions[0].GetString());
        Assert.DoesNotContain("\"operation\"", json);   // the pre-1.0 singular field is gone
    }

    [Fact]
    public void Text_marks_risk_levels_and_lists_messages()
    {
        var text = PlanRenderer.ToText(Sample);

        Assert.Contains("Plan: 2 change(s)", text);
        Assert.Contains("+ Create", text);
        Assert.Contains("! Drop", text);
        Assert.Contains("SQL72015", text);
    }

    [Fact]
    public void Text_for_empty_plan_says_no_changes()
    {
        var empty = new Plan(Plan.CurrentFormatVersion, Array.Empty<PlanAction>(), Array.Empty<PlanMessage>());

        Assert.Contains("No changes", PlanRenderer.ToText(empty));
    }

    // No incompleteness flag lives here on purpose: a comparison that could not
    // read the target never becomes a plan at all — DiffOperation fails on the
    // errors that report it, and apply refuses the same way. Pinned by
    // RestrictedComparisonIntegrationTests.

    // ------------------------------------------------------------- fingerprint
    // The apply gate's identity: same executable changes = same hash; messages
    // and descriptive fields must not perturb it.

    [Fact]
    public void Fingerprint_is_stable_and_survives_message_differences()
    {
        var withoutMessages = Sample with { Messages = Array.Empty<PlanMessage>() };

        Assert.Equal(PlanFingerprint.Compute(Sample), PlanFingerprint.Compute(withoutMessages));
    }

    [Fact]
    public void Fingerprint_changes_when_the_change_set_changes()
    {
        var different = Sample with
        {
            Actions = new[] { new PlanAction("dbo.Orders", "Table", PlanOperation.Create, RiskLevel.Safe) },
        };

        Assert.NotEqual(PlanFingerprint.Compute(Sample), PlanFingerprint.Compute(different));
    }

    [Fact]
    public void Json_planHash_matches_the_computed_fingerprint()
    {
        var json = PlanRenderer.ToJson(Sample);

        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal("1.4", doc.RootElement.GetProperty("formatVersion").GetString());
        Assert.Equal(PlanFingerprint.Compute(Sample), doc.RootElement.GetProperty("planHash").GetString());
    }

    // ------------------------------------------------------------- atomicity
    // Format 1.3 (ADR-0004 addendum): the plan declares what an apply of it
    // guarantees, so the resume story is read from the document, not assumed
    // from the tool.

    [Fact]
    public void Json_declares_the_apply_atomicity()
    {
        using var doc = System.Text.Json.JsonDocument.Parse(PlanRenderer.ToJson(Sample));
        Assert.Equal("partial", doc.RootElement.GetProperty("atomicity").GetString());

        using var transactional = System.Text.Json.JsonDocument.Parse(
            PlanRenderer.ToJson(Sample with { Atomicity = Core.Providers.ApplyAtomicity.Transactional }));
        Assert.Equal("transactional", transactional.RootElement.GetProperty("atomicity").GetString());
    }

    [Fact]
    public void Fingerprint_ignores_atomicity()
    {
        // The hash covers exactly what executes; atomicity is the provider's
        // static guarantee. A 1.2-era reviewed hash must still gate a 1.3 apply.
        var transactional = Sample with { Atomicity = Core.Providers.ApplyAtomicity.Transactional };

        Assert.Equal(PlanFingerprint.Compute(Sample), PlanFingerprint.Compute(transactional));
    }

    [Fact]
    public void Text_plan_prints_the_expect_plan_token()
    {
        Assert.Contains($"--expect-plan {PlanFingerprint.Compute(Sample)}", PlanRenderer.ToText(Sample));
    }
}
