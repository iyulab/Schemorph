using Schemorph.Core.Planning;
using Schemorph.Core.Providers;

namespace Schemorph.Core.Tests.Planning;

public class PlanBuilderTests
{
    private static CompareResult Result(params RawChange[] changes) =>
        new(changes, Array.Empty<RawMessage>(), UpdateScript: null);

    [Theory]
    [InlineData("Add", "Table", PlanOperation.Create, RiskLevel.Safe)]
    [InlineData("Change", "Table", PlanOperation.Alter, RiskLevel.Warning)]
    [InlineData("Redefine", "Procedure", PlanOperation.Redefine, RiskLevel.Safe)]
    // Dropping a programmable object is recoverable from source — warning, not
    // destructive (design principle §4: destructive = data-holding DROP only).
    [InlineData("Delete", "Procedure", PlanOperation.Drop, RiskLevel.Warning)]
    [InlineData("Delete", "View", PlanOperation.Drop, RiskLevel.Warning)]
    public void Classifies_operation_and_risk(string raw, string objectType, PlanOperation operation, RiskLevel risk)
    {
        var plan = PlanBuilder.Build(Result(new RawChange(raw, objectType, "dbo.T")), allowDestructive: false);

        var action = Assert.Single(plan.Actions);
        Assert.Equal(operation, action.Operation);
        Assert.Equal(risk, action.Risk);
    }

    [Fact]
    public void Destructive_change_is_gated_out_by_default_and_surfaced_as_message()
    {
        var plan = PlanBuilder.Build(Result(new RawChange("Delete", "Table", "dbo.LegacyLog")), allowDestructive: false);

        Assert.Empty(plan.Actions);
        var message = Assert.Single(plan.Messages);
        Assert.Equal("SCHEMORPH001", message.Code);
        Assert.Contains("dbo.LegacyLog", message.Text);
    }

    [Fact]
    public void Destructive_change_is_included_when_explicitly_allowed()
    {
        var plan = PlanBuilder.Build(Result(new RawChange("Delete", "Table", "dbo.LegacyLog")), allowDestructive: true);

        var action = Assert.Single(plan.Actions);
        Assert.Equal(RiskLevel.Destructive, action.Risk);
        Assert.True(plan.HasDestructiveChanges);
    }

    [Fact]
    public void Provider_messages_are_carried_onto_the_plan()
    {
        var result = new CompareResult(
            Array.Empty<RawChange>(),
            new[] { new RawMessage("Warning", "SQL72015", "data loss could occur") },
            UpdateScript: null);

        var plan = PlanBuilder.Build(result, allowDestructive: false);

        Assert.False(plan.HasChanges);
        var message = Assert.Single(plan.Messages);
        Assert.Equal("SQL72015", message.Code);
    }

    [Fact]
    public void Ledger_objects_are_invisible_to_plans()
    {
        var plan = PlanBuilder.Build(
            Result(new RawChange("Delete", "Table", "dbo.__SchemorphHistory")), allowDestructive: true);

        Assert.Empty(plan.Actions);
        Assert.Empty(plan.Messages);
    }

    [Theory]
    [InlineData("Delete", "Table", "dbo.Data", false, false)]     // destructive gated
    [InlineData("Delete", "Table", "dbo.Data", true, true)]       // destructive allowed
    [InlineData("Delete", "View", "dbo.V", false, true)]          // programmable drop = warning
    [InlineData("Add", "Table", "dbo.__SchemorphHistory", true, false)]   // ledger self-exclusion
    public void ShouldInclude_matches_plan_policy(string op, string type, string name, bool allowDestructive, bool expected)
    {
        Assert.Equal(expected, PlanBuilder.ShouldInclude(new RawChange(op, type, name), allowDestructive));
    }

    [Fact]
    public void Unknown_raw_operation_is_rejected()
    {
        Assert.Throws<ArgumentException>(() =>
            PlanBuilder.Build(Result(new RawChange("Explode", "Table", "dbo.T")), allowDestructive: false));
    }
}
