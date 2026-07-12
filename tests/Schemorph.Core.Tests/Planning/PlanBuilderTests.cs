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
    [InlineData("Delete", "View", "dbo.V", false, true)]          // programmable drop = warning, stays declarative
    [InlineData("Add", "Table", "dbo.__SchemorphHistory", true, false)]   // ledger self-exclusion
    [InlineData("Add", "Procedure", "dbo.P", false, false)]       // routed to redefine strategy
    [InlineData("Change", "View", "dbo.V", true, false)]          // routed to redefine strategy
    public void ShouldInclude_matches_plan_policy(string op, string type, string name, bool allowDestructive, bool expected)
    {
        Assert.Equal(expected, PlanBuilder.ShouldInclude(new RawChange(op, type, name), allowDestructive));
    }

    // ADR-0002 strategy routing: programmable-object creation/alteration never goes
    // through the declarative diff — it is applied via checksum + CREATE OR ALTER.
    // Drops stay declarative so that deleting a file is still honored.
    [Theory]
    [InlineData("Add", "Procedure", true)]
    [InlineData("Change", "Procedure", true)]
    [InlineData("Change", "View", true)]
    [InlineData("Add", "ScalarFunction", true)]
    [InlineData("Change", "TableValuedFunction", true)]
    [InlineData("Add", "DmlTrigger", true)]
    [InlineData("Delete", "Procedure", false)]
    [InlineData("Change", "Table", false)]
    [InlineData("Add", "Index", false)]
    public void RoutesToRedefine_covers_programmable_create_and_alter_only(string op, string type, bool expected)
    {
        Assert.Equal(expected, PlanBuilder.RoutesToRedefine(new RawChange(op, type, "dbo.X")));
    }

    [Fact]
    public void Programmable_create_and_alter_are_absent_from_declarative_actions()
    {
        var plan = PlanBuilder.Build(
            Result(new RawChange("Add", "Procedure", "dbo.P"), new RawChange("Change", "View", "dbo.V")),
            allowDestructive: false);

        Assert.Empty(plan.Actions);
        Assert.Empty(plan.Messages);
    }

    [Fact]
    public void Pending_redefines_are_merged_as_safe_redefine_actions_after_declarative_ones()
    {
        var pending = new[]
        {
            new ProgrammableObjectInfo("dbo.P", "Procedure", "p.sql", "CREATE OR ALTER ...", Array.Empty<string>()),
        };

        var plan = PlanBuilder.Build(
            Result(new RawChange("Add", "Table", "dbo.T")), allowDestructive: false, pending);

        Assert.Equal(2, plan.Actions.Count);
        Assert.Equal(PlanOperation.Create, plan.Actions[0].Operation);
        var redefine = plan.Actions[1];
        Assert.Equal(PlanOperation.Redefine, redefine.Operation);
        Assert.Equal(RiskLevel.Safe, redefine.Risk);
        Assert.Equal("dbo.P", redefine.ObjectName);
        Assert.Equal("Procedure", redefine.ObjectType);
    }

    [Fact]
    public void Unknown_raw_operation_is_rejected()
    {
        Assert.Throws<ArgumentException>(() =>
            PlanBuilder.Build(Result(new RawChange("Explode", "Table", "dbo.T")), allowDestructive: false));
    }
}
