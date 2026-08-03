using Schemorph.Core.Providers;

namespace Schemorph.Core.Planning;

/// <summary>What an action does to a database object.</summary>
public enum PlanOperation
{
    Create,
    Alter,
    Drop,
    /// <summary>Idempotent re-definition of a programmable object (CREATE OR ALTER).</summary>
    Redefine,
}

/// <summary>Risk classification carried by every planned action.</summary>
public enum RiskLevel
{
    Safe,
    Warning,
    Destructive,
}

/// <summary>A single planned change. The central unit of Schemorph's contract with humans and agents.</summary>
public sealed record PlanAction(
    string ObjectName,
    string ObjectType,
    PlanOperation Operation,
    RiskLevel Risk,
    string? Sql = null,
    string? Explanation = null);

/// <summary>Diagnostic attached to a plan (e.g. engine warnings, gated-out actions).</summary>
public sealed record PlanMessage(string Severity, string Code, string Text);

/// <summary>
/// An object the engine's update script has statements for that this plan will not
/// execute. Distinct from an apply's excluded changes: those are changes the plan
/// held and the run left out, whereas these never became actions at all — the plan
/// dropped them while the engine's text kept them.
/// </summary>
/// <param name="Reason">
/// Why it is not executed, in the reviewer's terms rather than an error code. The
/// reader is deciding whether to sign a document containing a statement they can
/// see, so the useful answer is what the tool does with it — not which branch
/// dropped it.
/// </param>
public sealed record PlanExclusion(string ObjectName, string Reason);

/// <summary>
/// The plan: every mutating operation is expressible as one of these before execution.
/// <c>diff</c> produces a plan and stops; <c>apply</c> produces a plan and executes it.
/// </summary>
/// <param name="Atomicity">
/// What an apply of this plan guarantees on partial failure (ADR-0004
/// addendum): the provider's declared mode, carried in the document so the
/// resume story is read from the plan instead of assumed from the tool.
/// Defaults to the weakest claim — a plan may under-claim, never over-claim.
/// Excluded from <see cref="PlanFingerprint"/>: it is a static property of the
/// provider, not part of which changes execute.
/// </param>
/// <param name="UpdateScript">
/// The declarative publish's executed text — exactly the SQL the reviewer reads
/// in the review script and the apply runs (the provider's whole update script).
/// Carried on the plan so <see cref="PlanFingerprint"/> can bind *what executes*,
/// not just the per-object action tuples: two plans that touch the same objects
/// with the same operations but different DDL are different plans, and the gate
/// must tell them apart. Null when there is nothing declarative to publish
/// (a programmable-only plan carries its scripts on the redefine actions
/// instead). NOT part of the serialized JSON model — the full text reaches
/// reviewers through the review script / <c>diff --format sql</c>; here it is a
/// fingerprint input only.
/// </param>
/// <param name="Excluded">
/// Objects the engine's update script contains statements for and this plan does
/// not execute. The script is the engine's own text, rendered verbatim so the
/// reviewed and executed artifacts stay one thing — which means it can carry DDL
/// for objects the plan drops, and a reader has no way to tell from the text
/// alone. Naming them is what keeps "reviewed" and "executed" honest about their
/// difference instead of leaving the reader to infer it from a DROP.
/// Excluded from <see cref="PlanFingerprint"/>: it is derived from the actions
/// and the script the fingerprint already binds, so it adds nothing to identity —
/// and a plan's hash must not move because it started explaining itself better.
/// </param>
public sealed record Plan(
    string FormatVersion,
    IReadOnlyList<PlanAction> Actions,
    IReadOnlyList<PlanMessage> Messages,
    ApplyAtomicity Atomicity = ApplyAtomicity.Partial,
    string? UpdateScript = null,
    IReadOnlyList<PlanExclusion>? Excluded = null)
{
    /// <summary>Never null; an empty list means the script and the plan agree.</summary>
    public IReadOnlyList<PlanExclusion> Excluded { get; init; } = Excluded ?? Array.Empty<PlanExclusion>();

    /// <summary>
    /// Version of the machine-readable plan format (docs/plan-format.md), following
    /// Terraform's convention: the minor version increments for backward-compatible
    /// additions (consumers must ignore unknown properties); the major version
    /// increments for breaking changes. Independent of the product version.
    /// </summary>
    public const string CurrentFormatVersion = "1.6";   // 1.6: excluded[] — objects the script contains and the plan does not execute (see docs/plan-format.md)

    public bool HasChanges => Actions.Count > 0;

    public bool HasDestructiveChanges => Actions.Any(a => a.Risk == RiskLevel.Destructive);
}
