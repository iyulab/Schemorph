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
        IReadOnlyList<PlanAction>? redefineActions = null,
        ApplyAtomicity atomicity = ApplyAtomicity.Partial)
    {
        var actions = new List<PlanAction>();
        // What the plan drops while the engine's script keeps it. Recorded where the
        // dropping happens: a reader of the review document sees those statements, so
        // deciding not to run something is only half of it — the other half is saying so.
        var excluded = new List<PlanExclusion>();
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
                // Schemorph's own bookkeeping is invisible to plans — but not to the
                // engine, which compares it like any other table and writes a DROP for
                // it into the update script whenever the target already has one (that
                // is, on every run after the first). Staying silent here is what left
                // reviewers reading a DROP of the history ledger with nothing in the
                // plan, the summary or the messages to say it does not run.
                excluded.Add(new PlanExclusion(change.ObjectName,
                    "Schemorph's own history ledger. It is not part of the desired state and is " +
                    "never dropped or altered by an apply; the engine reports it only because it " +
                    "compares the whole target."));
                continue;
            }
            if (RoutesToRedefine(change))
            {
                continue;   // Represented by checksum-driven Redefine actions instead.
            }

            var script = scripts.GetValueOrDefault(change.ObjectName);
            var (operation, risk) = Classify(change, script);
            if (risk == RiskLevel.Destructive && !allowDestructive)
            {
                // What is lost differs by shape, and the reviewer is deciding whether to
                // enable it — so the message says which loss they would be enabling
                // rather than only that one exists.
                var loss = script?.DropsColumn == true && operation == PlanOperation.Alter
                    ? "a column the desired state no longer declares is dropped, and its rows do not survive"
                    : "the object it drops holds data";

                messages.Add(new PlanMessage(
                    "Warning",
                    "SCHEMORPH001",
                    $"Destructive change excluded from plan (enable explicitly to include): " +
                    $"{operation} {change.ObjectType} {change.ObjectName} — {loss}."));
                // Same shape as the ledger above: the engine's script still carries the
                // statement. The warning says it was gated; this says where to expect it.
                excluded.Add(new PlanExclusion(change.ObjectName,
                    $"Destructive {operation} on {change.ObjectType} — {loss}, gated out of this plan. " +
                    "Enable destructive changes explicitly to include it."));
                continue;
            }

            actions.Add(new PlanAction(change.ObjectName, change.ObjectType, operation, risk,
                Sql: script?.Sql,
                Explanation: Explain(operation, risk, script?.Rebuild == true)));
        }

        // Redefines execute after the declarative publish; the plan mirrors that
        // order. The redefine strategy renders its own actions (it owns the "why").
        actions.AddRange(redefineActions ?? Array.Empty<PlanAction>());

        messages.AddRange(PlanLinter.Lint(actions, scripts));

        // The executed declarative script rides the plan so its fingerprint binds
        // exactly what runs, not just the object-level action shape (PlanFingerprint).
        return new Plan(Plan.CurrentFormatVersion, actions, messages, atomicity, compareResult.UpdateScript,
            excluded);
    }

    /// <summary>
    /// Plan explanations for the declarative path: deterministic rationale from
    /// the classification, sharpened by the provider's script attribution when
    /// it detected a rebuild (redefines carry their own explanation).
    /// </summary>
    private static string Explain(PlanOperation operation, RiskLevel risk, bool rebuild) => operation switch
    {
        // The loss comes first: a change can both rebuild and drop a column, and a
        // reader told only about the rebuild's cost has been told the cheaper half.
        PlanOperation.Alter when risk == RiskLevel.Destructive =>
            "The live definition differs from the desired state, and carrying the change out drops a column the desired state no longer declares — its rows are lost, while the table and its other columns survive. In this plan only because destructive changes were explicitly allowed."
            + (rebuild ? " The table is also rebuilt: a new table is created, rows are copied over, the old table is dropped and the new one renamed. Expect time and log proportional to the data." : ""),
        PlanOperation.Alter when rebuild =>
            "The change cannot be applied in place: the table is rebuilt — a new table is created, rows are copied over, the old table is dropped and the new one renamed. Expect time and log proportional to the data.",
        PlanOperation.Create => "Missing from the database; created by the declarative publish.",
        PlanOperation.Alter => "The live definition differs from the desired state; altered in place by the declarative publish.",
        PlanOperation.Drop when risk == RiskLevel.Destructive =>
            "Drops an object that holds data — its rows are lost. In this plan only because destructive changes were explicitly allowed.",
        PlanOperation.Drop => "Its desired-state file was removed; dropped by the declarative publish (no data is stored in it).",
        _ => "Planned by the declarative publish.",
    };

    /// <summary>
    /// Apply-time policy: exactly the declarative changes a plan would contain.
    /// Takes the same attribution <see cref="Build"/> classifies with, because a
    /// gate that judged less than the plan did would let through what the plan
    /// gated out — the two must reach the same verdict from the same input.
    /// </summary>
    public static bool ShouldInclude(RawChange change, ChangeScript? script, bool allowDestructive)
    {
        if (LedgerObjects.IsLedgerObject(change.ObjectName)) return false;
        if (RoutesToRedefine(change)) return false;
        var (_, risk) = Classify(change, script);
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

    public static (PlanOperation Operation, RiskLevel Risk) Classify(
        RawChange change, ChangeScript? script = null)
    {
        var operation = ParseOperation(change.Operation);
        return (operation, ClassifyRisk(operation, change.ObjectType, script));
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

    /// <summary>
    /// The criterion is data-losing, not object-dropping — and for most of this
    /// project's life those were treated as the same thing, because a plan is
    /// built per object and an object is the coarsest thing a change can name.
    /// A column removed from the desired state is carried out as an ALTER of the
    /// table that holds it: same object, same operation, and every row of that
    /// column gone. Classifying it from the operation alone therefore called the
    /// most common unrecoverable loss an ordinary alter and applied it by default.
    ///
    /// Which is why the provider's attribution is an input here. The signal it
    /// carries is deliberately narrow: a column the desired state no longer
    /// declares. A column that is *re-created* (<c>RecreatesColumn</c>) is not
    /// gated — its new values are the new definition's output, so they are
    /// replaced rather than lost, and <c>SCHEMORPH107</c> says so. The line is
    /// recoverability, not whether the old bytes survive.
    ///
    /// A provider that cannot prove the distinction reports neither signal and
    /// keeps the object-level classification it always had — under-claiming is
    /// the designed direction for every dialect judgment in
    /// <see cref="ChangeScript"/>.
    /// </summary>
    private static RiskLevel ClassifyRisk(
        PlanOperation operation, string objectType, ChangeScript? script) => operation switch
    {
        PlanOperation.Create => RiskLevel.Safe,
        PlanOperation.Redefine => RiskLevel.Safe,
        PlanOperation.Alter when script?.DropsColumn == true => RiskLevel.Destructive,
        PlanOperation.Alter => RiskLevel.Warning,
        PlanOperation.Drop when DataHoldingObjectTypes.Contains(objectType) => RiskLevel.Destructive,
        PlanOperation.Drop => RiskLevel.Warning,
        _ => RiskLevel.Warning,
    };
}
