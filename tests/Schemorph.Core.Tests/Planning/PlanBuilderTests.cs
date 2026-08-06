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

    /// <summary>
    /// Invisible to the plan is not invisible to the engine. It compares the ledger
    /// like any other table and writes a DROP for it into the update script on every
    /// run against a target that already has one — which is every real target. The
    /// plan has to say so, because the reviewer can see the statement.
    /// </summary>
    [Fact]
    public void A_dropped_ledger_change_is_recorded_as_excluded_from_execution()
    {
        var plan = PlanBuilder.Build(
            Result(new RawChange("Delete", "Table", "dbo.__SchemorphHistory")), allowDestructive: true);

        var excluded = Assert.Single(plan.Excluded);
        Assert.Equal("dbo.__SchemorphHistory", excluded.ObjectName);
        Assert.Contains("never dropped or altered", excluded.Reason);
    }

    /// <summary>
    /// The gated-destructive path drops a change the same way, and the script keeps
    /// its statements the same way — the warning says it was gated, this says the
    /// text is still there to read.
    /// </summary>
    [Fact]
    public void A_gated_destructive_change_is_recorded_as_excluded_from_execution()
    {
        var plan = PlanBuilder.Build(
            Result(new RawChange("Delete", "Table", "dbo.LegacyLog")), allowDestructive: false);

        var excluded = Assert.Single(plan.Excluded);
        Assert.Equal("dbo.LegacyLog", excluded.ObjectName);
        Assert.Contains("Enable destructive changes explicitly", excluded.Reason);
    }

    [Fact]
    public void A_plan_that_executes_everything_it_contains_excludes_nothing()
    {
        var plan = PlanBuilder.Build(
            Result(new RawChange("Add", "Table", "dbo.Orders")), allowDestructive: false);

        Assert.Empty(plan.Excluded);
        Assert.NotEmpty(plan.Actions);
    }

    /// <summary>
    /// Explaining itself better must not change a plan's identity: an operator holding
    /// a hash from before this field existed can still gate an apply with it.
    /// </summary>
    [Fact]
    public void Recording_exclusions_does_not_move_the_fingerprint()
    {
        var withExclusion = PlanBuilder.Build(
            Result(new RawChange("Add", "Table", "dbo.Orders"),
                   new RawChange("Delete", "Table", "dbo.__SchemorphHistory")),
            allowDestructive: true);
        var without = PlanBuilder.Build(
            Result(new RawChange("Add", "Table", "dbo.Orders")), allowDestructive: true);

        Assert.NotEmpty(withExclusion.Excluded);
        Assert.Equal(PlanFingerprint.Compute(without), PlanFingerprint.Compute(withExclusion));
    }

    [Theory]
    [InlineData("Delete", "Table", "dbo.Data", false, false, false)]   // destructive gated
    [InlineData("Delete", "Table", "dbo.Data", true, false, true)]     // destructive allowed
    [InlineData("Delete", "View", "dbo.V", false, false, true)]        // programmable drop = warning, stays declarative
    [InlineData("Add", "Table", "dbo.__SchemorphHistory", true, false, false)]   // ledger self-exclusion
    [InlineData("Add", "Procedure", "dbo.P", false, false, false)]     // routed to redefine strategy
    [InlineData("Change", "View", "dbo.V", true, false, false)]        // routed to redefine strategy
    [InlineData("Change", "Table", "dbo.Data", false, false, true)]    // ordinary in-place alter
    // The gate reads the attribution, not the operation: an alter that removes a
    // column is the same (Change, Table) tuple as the one above and loses every
    // row of it. Judging from the tuple alone applied it by default.
    [InlineData("Change", "Table", "dbo.Data", false, true, false)]    // column drop gated
    [InlineData("Change", "Table", "dbo.Data", true, true, true)]      // column drop allowed
    public void ShouldInclude_matches_plan_policy(
        string op, string type, string name, bool allowDestructive, bool dropsColumn, bool expected)
    {
        var script = dropsColumn ? new ChangeScript(name, "-- ddl", Rebuild: false, DropsColumn: true) : null;

        Assert.Equal(expected, PlanBuilder.ShouldInclude(new RawChange(op, type, name), script, allowDestructive));
    }

    /// <summary>
    /// The gate and the plan must reach the same verdict, because the apply filters
    /// with one and the reviewer signs the other. They did not always share input:
    /// the predicate took a <see cref="RawChange"/> while the plan also had the
    /// provider's attribution, so a column drop was gated out of the plan and
    /// applied anyway would have been indistinguishable from correct behaviour.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ShouldInclude_agrees_with_the_plan_it_gates(bool allowDestructive)
    {
        var change = new RawChange("Change", "Table", "dbo.Data");
        var script = new ChangeScript("dbo.Data", "ALTER TABLE dbo.Data DROP COLUMN Gone;",
            Rebuild: false, DropsColumn: true);
        var compare = new CompareResult([change], [], "ALTER TABLE dbo.Data DROP COLUMN Gone;", [script]);

        var plan = PlanBuilder.Build(compare, allowDestructive);
        var included = PlanBuilder.ShouldInclude(change, script, allowDestructive);

        Assert.Equal(plan.Actions.Any(a => a.ObjectName == "dbo.Data"), included);
    }

    [Fact]
    public void A_column_the_desired_state_stops_declaring_is_destructive()
    {
        var script = new ChangeScript("dbo.Data", "ALTER TABLE dbo.Data DROP COLUMN Gone;",
            Rebuild: false, DropsColumn: true);
        var compare = new CompareResult(
            [new RawChange("Change", "Table", "dbo.Data")], [],
            "ALTER TABLE dbo.Data DROP COLUMN Gone;", [script]);

        var gated = PlanBuilder.Build(compare, allowDestructive: false);
        Assert.Empty(gated.Actions);
        Assert.False(gated.HasDestructiveChanges);
        Assert.Contains(gated.Messages, m => m.Code == "SCHEMORPH001");
        // The statement stays in the engine's script, so the review document has to
        // name it — the 1.6 contract, exercised on the shape 1.6 did not yet cover.
        Assert.Contains(gated.Excluded, e => e.ObjectName == "dbo.Data");

        var allowed = PlanBuilder.Build(compare, allowDestructive: true);
        Assert.Equal(RiskLevel.Destructive, Assert.Single(allowed.Actions).Risk);
        Assert.True(allowed.HasDestructiveChanges);
        Assert.Contains(allowed.Messages, m => m.Code == "SCHEMORPH103");
    }

    /// <summary>
    /// The boundary the criterion draws: recoverability, not whether the old bytes
    /// survive. A re-created column's values are the new definition's output, so
    /// gating it would refuse a change nobody loses anything to — SCHEMORPH107
    /// describes it instead.
    /// </summary>
    [Fact]
    public void A_re_created_column_is_described_but_not_gated()
    {
        var script = new ChangeScript("dbo.Data", "-- drop and add", Rebuild: false,
            RecreatesColumn: true);
        var compare = new CompareResult(
            [new RawChange("Change", "Table", "dbo.Data")], [], "-- drop and add", [script]);

        var plan = PlanBuilder.Build(compare, allowDestructive: false);

        Assert.Equal(RiskLevel.Warning, Assert.Single(plan.Actions).Risk);
        Assert.Contains(plan.Messages, m => m.Code == "SCHEMORPH107");
        Assert.DoesNotContain(plan.Messages, m => m.Code == "SCHEMORPH001");
    }

    /// <summary>
    /// An index holds no data, so removing one is not gated — 0.7.0 measured that
    /// on both engines and said so. What was missing is that the band stayed silent
    /// about it too, and a reviewer who has learned that the band speaks up reads
    /// silence as "nothing at stake".
    /// </summary>
    [Fact]
    public void A_dropped_index_warns_without_gating()
    {
        var script = new ChangeScript("dbo.Data", "DROP INDEX ix_gone;", Rebuild: false,
            DropsIndex: true);
        var compare = new CompareResult(
            [new RawChange("Change", "Table", "dbo.Data")], [], "DROP INDEX ix_gone;", [script]);

        var plan = PlanBuilder.Build(compare, allowDestructive: false);

        Assert.Equal(RiskLevel.Warning, Assert.Single(plan.Actions).Risk);
        Assert.Contains(plan.Messages, m => m.Code == "SCHEMORPH108");
        Assert.DoesNotContain(plan.Messages, m => m.Code == "SCHEMORPH001");
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
                new ProgrammableObjectInfo("dbo.P", "Procedure", "p.sql", "CREATE ...", "CREATE OR ALTER ...", Array.Empty<string>()),
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
                new RawChange("Change", "Table", "dbo.Recast"),
                new RawChange("Delete", "Table", "dbo.Gone"),
            },
            Array.Empty<RawMessage>(), UpdateScript: "(whole)",
            ChangeScripts: new[]
            {
                new ChangeScript("dbo.Strict", "ALTER TABLE ...", Rebuild: false, AddsNotNullWithoutDefault: true),
                new ChangeScript("dbo.Rebuilt", "(rebuild sql)", Rebuild: true),
                new ChangeScript("dbo.Recast", "(drop and add a column)", Rebuild: false, RecreatesColumn: true),
            });

        var plan = PlanBuilder.Build(result, allowDestructive: true);

        Assert.Contains(plan.Messages, m => m.Code == "SCHEMORPH101" && m.Text.Contains("dbo.Strict"));
        Assert.Contains(plan.Messages, m => m.Code == "SCHEMORPH102" && m.Text.Contains("dbo.Rebuilt"));
        Assert.Contains(plan.Messages, m => m.Code == "SCHEMORPH103" && m.Text.Contains("dbo.Gone"));
        Assert.Contains(plan.Messages, m => m.Code == "SCHEMORPH107" && m.Text.Contains("dbo.Recast"));
        // The table-level entry that carries it still reads as one alter — which is
        // why the warning has to exist: only SCHEMORPH107 names the column loss.
        Assert.Equal(PlanOperation.Alter, plan.Actions.Single(a => a.ObjectName == "dbo.Recast").Operation);
        // Lint never escalates: warnings only, and the plan itself is untouched.
        Assert.All(plan.Messages, m => Assert.Equal("Warning", m.Severity));
        Assert.Equal(4, plan.Actions.Count);
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
