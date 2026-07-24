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
public sealed record Plan(
    string FormatVersion,
    IReadOnlyList<PlanAction> Actions,
    IReadOnlyList<PlanMessage> Messages,
    ApplyAtomicity Atomicity = ApplyAtomicity.Partial,
    string? UpdateScript = null)
{
    /// <summary>
    /// Version of the machine-readable plan format (docs/plan-format.md), following
    /// Terraform's convention: the minor version increments for backward-compatible
    /// additions (consumers must ignore unknown properties); the major version
    /// increments for breaking changes. Independent of the product version.
    /// </summary>
    public const string CurrentFormatVersion = "1.4";   // 1.4: planHash binds the executed script text (bugfix — see PlanFingerprint)

    public bool HasChanges => Actions.Count > 0;

    public bool HasDestructiveChanges => Actions.Any(a => a.Risk == RiskLevel.Destructive);
}
