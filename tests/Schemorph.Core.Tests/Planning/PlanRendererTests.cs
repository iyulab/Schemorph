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
    public void Json_carries_version_flags_actions_and_messages()
    {
        var json = PlanRenderer.ToJson(Sample);

        Assert.Contains("\"formatVersion\": \"0.1\"", json);
        Assert.Contains("\"hasChanges\": true", json);
        Assert.Contains("\"hasDestructiveChanges\": true", json);
        Assert.Contains("\"operation\": \"create\"", json);
        Assert.Contains("\"risk\": \"destructive\"", json);
        Assert.Contains("\"code\": \"SQL72015\"", json);
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
}
