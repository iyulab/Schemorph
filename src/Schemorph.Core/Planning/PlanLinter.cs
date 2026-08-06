using Schemorph.Core.Providers;

namespace Schemorph.Core.Planning;

/// <summary>
/// Safety lint over the plan (ROADMAP Phase 2): machine-checkable warnings in
/// the SCHEMORPH1xx band, attached to the plan's messages so every surface
/// (diff, status, apply preview, MCP, resources) carries them for free.
/// Deliberately conservative: a rule fires only on what is proven (from the
/// classification or the provider's dialect signals) — warnings never change
/// the exit code, and gating stays with the destructive gate.
///
/// What the band is about widened once, and the reason is worth keeping: it began
/// as "data that does not survive", which made silence readable as "nothing is
/// lost". Dropping an index loses no data and is correctly not gated — but the
/// queries it answered fall back to a scan, and a reviewer who has learned that
/// this band speaks up when something is at stake would read the silence as
/// approval. So the band covers <em>cost the apply changes</em>, of which data
/// loss is the most expensive kind rather than the only one. Gating is unaffected:
/// what fires here and what the destructive gate stops remain separate questions.
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

            if (script?.RecreatesColumn == true)
            {
                yield return new PlanMessage("Warning", "SCHEMORPH107",
                    $"{action.ObjectName}: a column is re-created rather than altered — " +
                    "its current values do not survive, though the table and its other columns do.");
            }

            if (script?.DropsIndex == true)
            {
                yield return new PlanMessage("Warning", "SCHEMORPH108",
                    $"{action.ObjectName}: an index the desired state does not declare is dropped — " +
                    "no data is lost, so this is not gated, but every query that relied on it " +
                    "falls back to a scan. Declare it in the desired state to keep it.");
            }

            if (action.Risk == RiskLevel.Destructive)
            {
                // Two shapes reach this, and they lose different amounts. Saying
                // "the data it holds" of a table alter that drops one column reads
                // as the whole table, which is the reverse of the misjudgment the
                // gate exists to prevent — and the more dangerous direction, since
                // it teaches the reader that the warning overstates.
                yield return new PlanMessage("Warning", "SCHEMORPH103",
                    script?.DropsColumn == true && action.Operation == PlanOperation.Alter
                        ? $"{action.ObjectName}: destructive change included in the plan — a column " +
                          "the desired state no longer declares is dropped and its rows are lost. " +
                          "The table and its other columns survive."
                        : $"{action.ObjectName}: destructive change included in the plan — " +
                          "applying it loses the data it holds.");
            }
        }
    }
}
