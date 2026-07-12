using Schemorph.Core.Providers;

namespace Schemorph.Core.Planning;

/// <summary>
/// Safety lint over the plan (ROADMAP Phase 2): machine-checkable warnings in
/// the SCHEMORPH1xx band, attached to the plan's messages so every surface
/// (diff, status, apply preview, MCP, resources) carries them for free.
/// Deliberately conservative: a rule fires only on what is proven (from the
/// classification or the provider's dialect signals) — warnings never change
/// the exit code, and gating stays with the destructive gate.
/// </summary>
public static class PlanLinter
{
    public static IEnumerable<PlanMessage> Lint(
        IReadOnlyList<PlanAction> actions,
        IReadOnlyDictionary<string, ChangeScript> scripts)
    {
        foreach (var action in actions)
        {
            var script = scripts.GetValueOrDefault(action.ObjectName);

            if (script?.AddsNotNullWithoutDefault == true)
            {
                yield return new PlanMessage("Warning", "SCHEMORPH101",
                    $"{action.ObjectName}: adds a NOT NULL column without a default — " +
                    "this fails on a table that already holds rows. Add a DEFAULT or make it NULLable first.");
            }

            if (script?.Rebuild == true)
            {
                yield return new PlanMessage("Warning", "SCHEMORPH102",
                    $"{action.ObjectName}: this change rebuilds the table (new table, rows copied, " +
                    "old dropped, renamed) — time, locks and transaction log grow with the data.");
            }

            if (action.Risk == RiskLevel.Destructive)
            {
                yield return new PlanMessage("Warning", "SCHEMORPH103",
                    $"{action.ObjectName}: destructive change included in the plan — " +
                    "applying it loses the data it holds.");
            }
        }
    }
}
