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
public sealed record Plan(
    string FormatVersion,
    IReadOnlyList<PlanAction> Actions,
    IReadOnlyList<PlanMessage> Messages,
    ApplyAtomicity Atomicity = ApplyAtomicity.Partial)
{
    /// <summary>
    /// Version of the machine-readable plan format (docs/plan-format.md), following
    /// Terraform's convention: the minor version increments for backward-compatible
    /// additions (consumers must ignore unknown properties); the major version
    /// increments for breaking changes. Independent of the product version.
    /// </summary>
    public const string CurrentFormatVersion = "1.3";   // 1.3: atomicity declared (additive; excluded from planHash)

    public bool HasChanges => Actions.Count > 0;

    public bool HasDestructiveChanges => Actions.Any(a => a.Risk == RiskLevel.Destructive);
}
