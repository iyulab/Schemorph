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
        var withheld = Withheld(
            compareResult.Changes.Select(c => (c, Gate(c, scripts.GetValueOrDefault(c.ObjectName), allowDestructive))),
            compareResult.ExclusionDependencies);

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
            if (withheld.TryGetValue(change.ObjectName, out var cause))
            {
                // Not gated on its own account: it is in the way of a change that is. The
                // engine leaves a change out only with what depends on it, so running this
                // one would carry the withheld change out with it — reported here as
                // excluded and then executed, which is the one outcome the gate exists to
                // rule out.
                messages.Add(new PlanMessage(
                    "Warning",
                    "SCHEMORPH015",
                    $"{change.ObjectName} is withheld with {cause}: it depends on {cause}, whose change is " +
                    "withheld from this plan, and the engine leaves a change out only together with what depends " +
                    $"on it. Both run once {cause}'s change is in the plan (enable destructive changes), or once " +
                    "the desired state no longer asks for that change.",
                    change.ObjectName));
                excluded.Add(new PlanExclusion(change.ObjectName,
                    $"Withheld with {cause}: this change depends on {cause}, whose change is withheld from this " +
                    $"plan, and the engine cannot leave {cause}'s change out while this one runs."));
                continue;
            }

            var script = scripts.GetValueOrDefault(change.ObjectName);
            var (operation, risk) = Classify(change, script);
            var verdict = Gate(change, script, allowDestructive);
            if (verdict == ChangeInclusion.IncludeWithoutDestructive)
            {
                // Only the column drop is gated. The rest of the table's change — a column
                // added beside it, a default changed — loses nothing, and holding it back
                // with the drop left the desired state's additive half unapplied while a
                // re-diff reported nothing applicable. The drop is still refused, and still
                // said so, in the same words as a whole-object refusal.
                messages.Add(new PlanMessage(
                    "Warning",
                    "SCHEMORPH001",
                    $"Destructive change excluded from plan (enable explicitly to include): " +
                    $"{operation} {change.ObjectType} {change.ObjectName} — a column the desired state " +
                    "no longer declares is dropped, and its rows do not survive. Only the column drop " +
                    "is withheld; the rest of this table's change is in the plan.",
                    change.ObjectName));
                excluded.Add(new PlanExclusion(change.ObjectName,
                    "Column drop gated out of this plan — its rows would not survive. The rest of this " +
                    "table's change runs. Enable destructive changes explicitly to include the drop.",
                    script!.DestructiveSql));
                if (script.AddsColumn)
                {
                    messages.Add(RenameWarning(change.ObjectName));
                }
                actions.Add(new PlanAction(change.ObjectName, change.ObjectType, operation, RiskLevel.Warning,
                    Sql: script.RemainderSql,
                    Explanation: Explain(operation, RiskLevel.Warning, script.Rebuild)
                        + " A column the desired state no longer declares is kept: dropping it is destructive and was not enabled.",
                    StatementCount: script.RemainderStatementCount));
                continue;
            }
            if (verdict == ChangeInclusion.Exclude)
            {
                // What is lost differs by shape, and the reviewer is deciding whether to
                // enable it — so the message says which loss they would be enabling
                // rather than only that one exists.
                var loss = script?.DropsColumn == true && operation == PlanOperation.Alter
                    ? "a column the desired state no longer declares is dropped, and its rows do not survive"
                    : "the object it drops holds data";

                // A provider that cannot separate the drop from the rest of the table's
                // change gates the whole object — so whatever else that change carried is
                // held back too. The provider counted the statements; saying so is not a guess.
                var withheldAlongside = script is { DropsColumn: true, StatementCount: > 1 }
                    && operation == PlanOperation.Alter
                    ? $" The rest of this table's change ({script.StatementCount - 1} other statement(s)) is " +
                      "withheld with it: this provider cannot separate the column drop from it."
                    : "";

                messages.Add(new PlanMessage(
                    "Warning",
                    "SCHEMORPH001",
                    $"Destructive change excluded from plan (enable explicitly to include): " +
                    $"{operation} {change.ObjectType} {change.ObjectName} — {loss}.{withheldAlongside}",
                    change.ObjectName));
                // Same shape as the ledger above: the engine's script still carries the
                // statement. The warning says it was gated; this says where to expect it.
                excluded.Add(new PlanExclusion(change.ObjectName,
                    $"Destructive {operation} on {change.ObjectType} — {loss}, gated out of this plan. " +
                    "Enable destructive changes explicitly to include it."));
                continue;
            }

            actions.Add(new PlanAction(change.ObjectName, change.ObjectType, operation, risk,
                Sql: script?.Sql,
                Explanation: Explain(operation, risk, script?.Rebuild == true),
                StatementCount: script?.StatementCount));
        }

        AddWithheldColumnDrops(compareResult, scripts, actions, messages, excluded);

        messages.AddRange(PossibleRenames(compareResult.Changes, scripts));

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
    /// The column drops the provider kept out of the comparison itself
    /// (<see cref="CompareResult.WithheldColumnDrops"/>), said the way a column drop the gate separates is
    /// said: <c>SCHEMORPH001</c>, an exclusion carrying the statements that do not run, and — beside a
    /// column added to the same table — <c>SCHEMORPH012</c>. What differs is where the rest of the table's
    /// change is read from: the comparison already is the one without the drop, so a table that appears
    /// among its changes has an action of its own and one that does not has nothing else to do.
    /// </summary>
    private static void AddWithheldColumnDrops(
        CompareResult compareResult, IReadOnlyDictionary<string, ChangeScript> scripts,
        List<PlanAction> actions, List<PlanMessage> messages, List<PlanExclusion> excluded)
    {
        foreach (var drop in (compareResult.WithheldColumnDrops ?? Array.Empty<WithheldColumnDrop>())
                     .OrderBy(d => d.ObjectName, StringComparer.Ordinal))
        {
            var rest = actions.Any(a => string.Equals(a.ObjectName, drop.ObjectName, StringComparison.OrdinalIgnoreCase))
                ? "the rest of this table's change is in the plan"
                : "nothing else in this table changes";
            messages.Add(new PlanMessage(
                "Warning",
                "SCHEMORPH001",
                "Destructive change excluded from plan (enable explicitly to include): " +
                $"Alter Table {drop.ObjectName} — a column the desired state no longer declares is dropped, " +
                $"and its rows do not survive. Only the column drop is withheld; {rest}.",
                drop.ObjectName));
            excluded.Add(new PlanExclusion(drop.ObjectName,
                "Column drop gated out of this plan — its rows would not survive. " +
                (rest.StartsWith("the rest", StringComparison.Ordinal)
                    ? "The rest of this table's change runs. "
                    : "Nothing else in this table changes. ") +
                "Enable destructive changes explicitly to include the drop.",
                drop.Statements));
            if (scripts.GetValueOrDefault(drop.ObjectName) is { AddsColumn: true })
            {
                messages.Add(RenameWarning(drop.ObjectName));
            }
        }
    }

    /// <summary>
    /// <c>SCHEMORPH012</c>: the column analogue of <c>SCHEMORPH011</c>. A rename arrives as an added
    /// column beside a dropped one, and with the drop withheld the addition runs alone: a new, empty
    /// column next to the old one that still holds the values. That is the first half of
    /// expand/contract, which is fine if it is what the reader meant — and a surprise if they meant a
    /// rename and planned to issue it by hand after the apply.
    /// </summary>
    private static PlanMessage RenameWarning(string objectName) => new(
        "Warning",
        "SCHEMORPH012",
        $"{objectName}: a column is added while a column the desired state no longer " +
        "declares is kept. If the added column is the kept one renamed, applying this plan adds " +
        "it empty and leaves the values where they are: rename the column in the database first, " +
        "then diff again — or, after this apply, copy the values across before enabling the drop.",
        objectName);

    /// <summary>
    /// <c>SCHEMORPH011</c>: the plan creates a table while dropping one that holds data — the shape
    /// a table rename takes in a desired-state diff.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Desired state names what should exist, not what it used to be called, so a renamed table
    /// arrives as a new table plus a missing one. The drop is gated (or, with destructive changes
    /// allowed, carried out), which protects the rows but does not move them: applying the plan as
    /// it stands leaves the data in the old table and an empty new one — or, allowed, loses it.
    /// Nothing here can tell a rename from an unrelated create and drop in the same change set, so
    /// the message says what to do <em>if</em> it is one, once per dropped table, and names every
    /// table being created as a candidate.
    /// </para>
    /// <para>
    /// Pairing by column shape would narrow the candidates, but the comparison reaches this layer as
    /// object names only; that needs the provider to report shapes, and is not attempted here.
    /// </para>
    /// </remarks>
    private static IEnumerable<PlanMessage> PossibleRenames(
        IReadOnlyList<RawChange> changes, IReadOnlyDictionary<string, ChangeScript> scripts)
    {
        var relevant = changes.Where(c => !LedgerObjects.IsLedgerObject(c.ObjectName)
            && string.Equals(c.ObjectType, "Table", StringComparison.OrdinalIgnoreCase)).ToList();
        var created = relevant
            .Where(c => Classify(c).Operation == PlanOperation.Create)
            .Select(c => c.ObjectName)
            .ToList();
        if (created.Count == 0) yield break;

        foreach (var dropped in relevant.Where(c =>
            Classify(c, scripts.GetValueOrDefault(c.ObjectName)).Risk == RiskLevel.Destructive
            && Classify(c).Operation == PlanOperation.Drop))
        {
            yield return new PlanMessage(
                "Warning",
                "SCHEMORPH011",
                $"Table {dropped.ObjectName} holds data and is dropped by the desired state while "
                + $"{string.Join(", ", created)} {(created.Count == 1 ? "is" : "are")} created. If one of them is "
                + $"{dropped.ObjectName} renamed, this plan does not move its rows: rename the table in the "
                + "database first (and any constraint named after it), then diff again.",
                dropped.ObjectName);
        }
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
    /// The verdict on one declarative change — the single place it is reached. The plan
    /// is built from it (<see cref="Build"/>) and the apply is gated by it, with the same
    /// attribution as input, because a gate that judged less than the plan did would let
    /// through what the plan gated out: the two must reach the same verdict from the same
    /// input.
    /// </summary>
    /// <remarks>
    /// A destructive change is withheld whole unless the provider separated its
    /// data-losing statements from the rest (<see cref="ChangeScript.DestructiveSql"/>) and
    /// something remains to run — then only those statements are withheld.
    /// </remarks>
    public static ChangeInclusion Gate(RawChange change, ChangeScript? script, bool allowDestructive)
    {
        if (LedgerObjects.IsLedgerObject(change.ObjectName)) return ChangeInclusion.Exclude;
        if (RoutesToRedefine(change)) return ChangeInclusion.Exclude;
        var (operation, risk) = Classify(change, script);
        if (risk != RiskLevel.Destructive || allowDestructive) return ChangeInclusion.Include;
        return operation == PlanOperation.Alter
            && script is { DestructiveSql: not null, RemainderSql: not null }
            ? ChangeInclusion.IncludeWithoutDestructive
            : ChangeInclusion.Exclude;
    }

    /// <summary>
    /// The changes withheld not on their own account but because a change they depend on is:
    /// each mapped to the change that put it there (the first one excluded on its own
    /// account, when the chain is longer). Shared by the plan and the apply for the same
    /// reason <see cref="Gate"/> is — the plan's account of what is left out and the apply's
    /// execution of it must come from one judgment.
    /// </summary>
    /// <remarks>
    /// <paramref name="dependencies"/> is the provider's map (<see cref="CompareResult.ExclusionDependencies"/>):
    /// withholding a change withholds the changes listed under it, and theirs in turn. A change
    /// listed there that is not part of the comparison is ignored — only changes can be withheld.
    /// </remarks>
    public static IReadOnlyDictionary<string, string> Withheld(
        IEnumerable<(RawChange Change, ChangeInclusion Verdict)> verdicts,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? dependencies)
    {
        var withheld = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (dependencies is null || dependencies.Count == 0) return withheld;

        var all = verdicts.ToList();
        // A change routed to re-definition is left out of the declarative publish only because
        // strategy 2 carries it out next; the engine running it as well changes nothing the plan
        // promised. So it neither withholds what depends on it nor is withheld itself.
        var names = all.Where(v => !RoutesToRedefine(v.Change))
            .Select(v => v.Change.ObjectName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var roots = all.Where(v => v.Verdict == ChangeInclusion.Exclude && names.Contains(v.Change.ObjectName))
            .Select(v => v.Change.ObjectName)
            .ToList();
        var excluded = roots.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var pending = new Queue<string>(roots);
        while (pending.Count > 0)
        {
            var name = pending.Dequeue();
            if (!dependencies.TryGetValue(name, out var dependents)) continue;
            var cause = withheld.GetValueOrDefault(name, name);
            foreach (var dependent in dependents)
            {
                if (!names.Contains(dependent) || !excluded.Add(dependent)) continue;
                withheld[dependent] = cause;
                pending.Enqueue(dependent);
            }
        }
        return withheld;
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
