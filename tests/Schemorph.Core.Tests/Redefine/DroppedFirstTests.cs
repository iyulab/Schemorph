using Schemorph.Core.Planning;
using Schemorph.Core.Providers;
using Schemorph.Core.Redefine;

namespace Schemorph.Core.Tests.Redefine;

/// <summary>
/// An object the declarative script drops ahead of its own statements
/// (<see cref="CompareResult.ProgrammablesDroppedFirst"/>) is gone by the time re-definition
/// runs, so it must be re-created whatever its checksum says — and the plan must say why.
/// </summary>
public sealed class DroppedFirstTests
{
    private static ProgrammableObjectInfo View(string name, params string[] dependsOn) =>
        new(name, "View", $"{name}.sql", $"body of {name}", $"CREATE OR REPLACE VIEW {name} AS SELECT 1", dependsOn);

    private static readonly ProgrammableAnalysis Analysis = new(
        new[] { View("inner"), View("outer", "inner"), View("other") }, Array.Empty<RawMessage>());

    [Fact]
    public void An_up_to_date_object_dropped_first_is_re_created_and_explained()
    {
        var plan = new RedefinePlan(Array.Empty<PendingRedefine>(), new[] { Analysis.Objects[2] });

        var result = RedefineRunner.WithInvalidations(plan, Analysis, tablesWithColumnChanges: null,
            droppedFirst: new[] { "outer", "inner" });

        Assert.Equal(new[] { "inner", "outer" }, result.Pending.Select(p => p.Object.ObjectName));
        Assert.All(result.Pending, p => Assert.True(p.DroppedFirst));
        var action = result.Pending[0].ToPlanAction();
        Assert.Equal(RiskLevel.Warning, action.Risk);
        Assert.Contains("dropped earlier in this apply", action.Explanation);
        // Untouched: not dropped, still just recorded as matching.
        Assert.Equal("other", Assert.Single(result.Reconcilable).ObjectName);
    }

    [Fact]
    public void An_object_already_pending_keeps_its_reason_and_is_marked()
    {
        var plan = new RedefinePlan(
            new[] { new PendingRedefine(Analysis.Objects[0], RedefineReason.ChecksumChanged) },
            Array.Empty<ProgrammableObjectInfo>());

        var result = RedefineRunner.WithInvalidations(plan, Analysis, null, new[] { "inner" });

        var pending = Assert.Single(result.Pending);
        Assert.Equal(RedefineReason.ChecksumChanged, pending.Reason);
        Assert.True(pending.DroppedFirst);
    }

    [Fact]
    public void A_dropped_object_is_never_merely_recorded_as_matching()
    {
        var plan = new RedefinePlan(Array.Empty<PendingRedefine>(), new[] { Analysis.Objects[0] });

        var result = RedefineRunner.WithInvalidations(plan, Analysis, null, new[] { "inner" });

        Assert.Empty(result.Reconcilable);
        Assert.Equal("inner", Assert.Single(result.Pending).Object.ObjectName);
    }

    [Fact]
    public void Nothing_dropped_first_leaves_the_plan_as_it_was()
    {
        var plan = new RedefinePlan(Array.Empty<PendingRedefine>(), new[] { Analysis.Objects[0] });

        Assert.Same(plan, RedefineRunner.WithInvalidations(plan, Analysis, null, null));
        Assert.Same(plan, RedefineRunner.WithInvalidations(plan, Analysis, null, Array.Empty<string>()));
    }
}
