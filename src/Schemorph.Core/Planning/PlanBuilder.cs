using Schemorph.Core.Ledger;
using Schemorph.Core.Providers;

namespace Schemorph.Core.Planning;

/// <summary>
/// Turns a provider's raw comparison into a Schemorph plan: classifies risk and
/// enforces destructive gating (destructive actions are excluded from the plan
/// unless explicitly allowed, and their exclusion is always visible as a message).
/// Schemorph's own ledger objects are never part of a plan.
/// </summary>
public static class PlanBuilder
{
    public static Plan Build(CompareResult compareResult, bool allowDestructive)
    {
        var actions = new List<PlanAction>();
        var messages = compareResult.Messages
            .Where(m => !LedgerObjects.IsLedgerObject(m.Text))   // engine chatter about our own bookkeeping
            .Select(m => new PlanMessage(m.Severity, m.Code, m.Text))
            .ToList();

        foreach (var change in compareResult.Changes)
        {
            if (LedgerObjects.IsLedgerObject(change.ObjectName))
            {
                continue;   // Schemorph's own bookkeeping is invisible to plans.
            }

            var (operation, risk) = Classify(change);
            if (risk == RiskLevel.Destructive && !allowDestructive)
            {
                messages.Add(new PlanMessage(
                    "Warning",
                    "SCHEMORPH001",
                    $"Destructive change excluded from plan (enable explicitly to include): {operation} {change.ObjectType} {change.ObjectName}"));
                continue;
            }

            actions.Add(new PlanAction(change.ObjectName, change.ObjectType, operation, risk));
        }

        return new Plan(Plan.CurrentFormatVersion, actions, messages);
    }

    /// <summary>Apply-time policy: exactly the changes a plan would contain.</summary>
    public static bool ShouldInclude(RawChange change, bool allowDestructive)
    {
        if (LedgerObjects.IsLedgerObject(change.ObjectName)) return false;
        var (_, risk) = Classify(change);
        return risk != RiskLevel.Destructive || allowDestructive;
    }

    public static (PlanOperation Operation, RiskLevel Risk) Classify(RawChange change)
    {
        var operation = ParseOperation(change.Operation);
        return (operation, ClassifyRisk(operation, change.ObjectType));
    }

    private static PlanOperation ParseOperation(string operation) => operation.ToLowerInvariant() switch
    {
        "add" or "create" => PlanOperation.Create,
        "change" or "alter" => PlanOperation.Alter,
        "delete" or "drop" => PlanOperation.Drop,
        "redefine" => PlanOperation.Redefine,
        _ => throw new ArgumentException($"Unknown raw operation '{operation}'.", nameof(operation)),
    };

    /// <summary>
    /// Object types whose DROP loses data (design principle §4: destructive =
    /// "DROP of anything holding data"). Dropping programmable objects is
    /// recoverable from source and therefore a warning, not destructive.
    /// </summary>
    private static readonly HashSet<string> DataHoldingObjectTypes =
        new(StringComparer.OrdinalIgnoreCase) { "Table" };

    private static RiskLevel ClassifyRisk(PlanOperation operation, string objectType) => operation switch
    {
        PlanOperation.Create => RiskLevel.Safe,
        PlanOperation.Redefine => RiskLevel.Safe,
        PlanOperation.Alter => RiskLevel.Warning,
        PlanOperation.Drop when DataHoldingObjectTypes.Contains(objectType) => RiskLevel.Destructive,
        PlanOperation.Drop => RiskLevel.Warning,
        _ => RiskLevel.Warning,
    };
}
