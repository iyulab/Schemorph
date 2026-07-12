using Schemorph.Core.Planning;
using Schemorph.Core.Providers;
using Schemorph.Core.Redefine;

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
            new PendingRedefine(
                new ProgrammableObjectInfo("dbo.P", "Procedure", "p.sql", "CREATE OR ALTER ...", Array.Empty<string>()),
                RedefineReason.ChecksumChanged).ToPlanAction(),
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
        // Plan explanations: the redefine carries its exact script and rationale.
        Assert.Equal("CREATE OR ALTER ...", redefine.Sql);
        Assert.Contains("checksum", redefine.Explanation);
    }

    [Fact]
    public void Every_change_carries_a_deterministic_explanation()
    {
        var plan = PlanBuilder.Build(
            Result(
                new RawChange("Add", "Table", "dbo.New"),
                new RawChange("Change", "Table", "dbo.Edited"),
                new RawChange("Delete", "Table", "dbo.Gone")),
            allowDestructive: true);

        Assert.All(plan.Actions, a => Assert.False(string.IsNullOrWhiteSpace(a.Explanation)));
        var drop = plan.Actions.Single(a => a.Operation == PlanOperation.Drop);
        Assert.Contains("rows are lost", drop.Explanation);
        // Declarative SQL decomposition is not implemented yet — sql stays null here.
        Assert.All(plan.Actions, a => Assert.Null(a.Sql));
    }

    [Fact]
    public void Change_scripts_join_per_change_sql_and_sharpen_rebuild_explanations()
    {
        var result = new CompareResult(
            new[]
            {
                new RawChange("Change", "Table", "dbo.Rebuilt"),
                new RawChange("Change", "Table", "dbo.Plain"),
            },
            Array.Empty<RawMessage>(), UpdateScript: "(whole script)",
            ChangeScripts: new[] { new ChangeScript("dbo.Rebuilt", "CREATE TABLE [dbo].[tmp_ms_xx_Rebuilt] ...", Rebuild: true) });

        var plan = PlanBuilder.Build(result, allowDestructive: false);

        var rebuilt = plan.Actions.Single(a => a.ObjectName == "dbo.Rebuilt");
        Assert.Contains("tmp_ms_xx_Rebuilt", rebuilt.Sql);
        Assert.Contains("rebuilt", rebuilt.Explanation);
        // Unattributed changes stay honestly silent on sql, generic on explanation.
        var plain = plan.Actions.Single(a => a.ObjectName == "dbo.Plain");
        Assert.Null(plain.Sql);
        Assert.Contains("altered in place", plain.Explanation);
    }

    [Fact]
    public void Safety_lint_warnings_ride_the_plan_messages()
    {
        var result = new CompareResult(
            new[]
            {
                new RawChange("Change", "Table", "dbo.Strict"),
                new RawChange("Change", "Table", "dbo.Rebuilt"),
                new RawChange("Delete", "Table", "dbo.Gone"),
            },
            Array.Empty<RawMessage>(), UpdateScript: "(whole)",
            ChangeScripts: new[]
            {
                new ChangeScript("dbo.Strict", "ALTER TABLE ...", Rebuild: false, AddsNotNullWithoutDefault: true),
                new ChangeScript("dbo.Rebuilt", "(rebuild sql)", Rebuild: true),
            });

        var plan = PlanBuilder.Build(result, allowDestructive: true);

        Assert.Contains(plan.Messages, m => m.Code == "SCHEMORPH101" && m.Text.Contains("dbo.Strict"));
        Assert.Contains(plan.Messages, m => m.Code == "SCHEMORPH102" && m.Text.Contains("dbo.Rebuilt"));
        Assert.Contains(plan.Messages, m => m.Code == "SCHEMORPH103" && m.Text.Contains("dbo.Gone"));
        // Lint never escalates: warnings only, and the plan itself is untouched.
        Assert.All(plan.Messages, m => Assert.Equal("Warning", m.Severity));
        Assert.Equal(3, plan.Actions.Count);
    }

    [Fact]
    public void A_clean_plan_lints_clean()
    {
        var plan = PlanBuilder.Build(
            Result(new RawChange("Add", "Table", "dbo.Fresh")), allowDestructive: false);

        Assert.Empty(plan.Messages);
    }

    [Fact]
    public void Unknown_raw_operation_is_rejected()
    {
        Assert.Throws<ArgumentException>(() =>
            PlanBuilder.Build(Result(new RawChange("Explode", "Table", "dbo.T")), allowDestructive: false));
    }
}
