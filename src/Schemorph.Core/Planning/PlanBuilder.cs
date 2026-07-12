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
    public static Plan Build(
        CompareResult compareResult,
        bool allowDestructive,
        IReadOnlyList<PlanAction>? redefineActions = null)
    {
        var actions = new List<PlanAction>();
        var messages = compareResult.Messages
            .Where(m => !LedgerObjects.IsLedgerObject(m.Text))   // engine chatter about our own bookkeeping
            .Select(m => new PlanMessage(m.Severity, m.Code, m.Text))
            .ToList();
        var scripts = (compareResult.ChangeScripts ?? Array.Empty<ChangeScript>())
            .ToDictionary(s => s.ObjectName, StringComparer.OrdinalIgnoreCase);

        foreach (var change in compareResult.Changes)
        {
            if (LedgerObjects.IsLedgerObject(change.ObjectName))
            {
                continue;   // Schemorph's own bookkeeping is invisible to plans.
            }
            if (RoutesToRedefine(change))
            {
                continue;   // Represented by checksum-driven Redefine actions instead.
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

            var script = scripts.GetValueOrDefault(change.ObjectName);
            actions.Add(new PlanAction(change.ObjectName, change.ObjectType, operation, risk,
                Sql: script?.Sql,
                Explanation: Explain(operation, risk, script?.Rebuild == true)));
        }

        // Redefines execute after the declarative publish; the plan mirrors that
        // order. The redefine strategy renders its own actions (it owns the "why").
        actions.AddRange(redefineActions ?? Array.Empty<PlanAction>());

        messages.AddRange(PlanLinter.Lint(actions, scripts));

        return new Plan(Plan.CurrentFormatVersion, actions, messages);
    }

    /// <summary>
    /// Plan explanations for the declarative path: deterministic rationale from
    /// the classification, sharpened by the provider's script attribution when
    /// it detected a rebuild (redefines carry their own explanation).
    /// </summary>
    private static string Explain(PlanOperation operation, RiskLevel risk, bool rebuild) => operation switch
    {
        PlanOperation.Alter when rebuild =>
            "The change cannot be applied in place: the table is rebuilt — a new table is created, rows are copied over, the old table is dropped and the new one renamed. Expect time and log proportional to the data.",
        PlanOperation.Create => "Missing from the database; created by the declarative publish.",
        PlanOperation.Alter => "The live definition differs from the desired state; altered in place by the declarative publish.",
        PlanOperation.Drop when risk == RiskLevel.Destructive =>
            "Drops an object that holds data — its rows are lost. In this plan only because destructive changes were explicitly allowed.",
        PlanOperation.Drop => "Its desired-state file was removed; dropped by the declarative publish (no data is stored in it).",
        _ => "Planned by the declarative publish.",
    };

    /// <summary>Apply-time policy: exactly the declarative changes a plan would contain.</summary>
    public static bool ShouldInclude(RawChange change, bool allowDestructive)
    {
        if (LedgerObjects.IsLedgerObject(change.ObjectName)) return false;
        if (RoutesToRedefine(change)) return false;
        var (_, risk) = Classify(change);
        return risk != RiskLevel.Destructive || allowDestructive;
    }

    /// <summary>
    /// ADR-0002 strategy routing: creating or altering a programmable object goes
    /// through idempotent re-definition, never the declarative diff. Drops stay
    /// declarative so deleting a file is still honored.
    /// </summary>
    public static bool RoutesToRedefine(RawChange change) =>
        ProgrammableObjects.IsProgrammable(change.ObjectType)
        && Classify(change).Operation is PlanOperation.Create or PlanOperation.Alter or PlanOperation.Redefine;

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
