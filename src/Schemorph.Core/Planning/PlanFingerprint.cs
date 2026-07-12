namespace Schemorph.Core.Planning;

/// <summary>
/// The plan's identity for the apply gate (`--expect-plan`, MCP apply): a
/// SHA-256 over exactly what would execute — each change's name, type, action
/// list and risk, in plan order. Messages and the descriptive sql/explanation
/// fields are excluded: two plans that execute the same changes ARE the same
/// plan. Reviewing a diff and applying its hash guarantees the apply runs the
/// reviewed changes or refuses (no diff-apply race).
/// </summary>
public static class PlanFingerprint
{
    public static string Compute(Plan plan) =>
        ContentChecksum.Compute(string.Join("\n", plan.Actions.Select(a =>
            $"{a.ObjectName}|{a.ObjectType}|{a.Operation}|{a.Risk}")));
}

/// <summary>
/// Thrown by the apply gate when the computed plan differs from the expected
/// fingerprint — from inside the pre-publish hook, so nothing has executed.
/// </summary>
public sealed class PlanMismatchException(string expected, string actual)
    : Exception($"The plan changed since it was reviewed (expected {expected}, computed {actual}). Nothing was applied.")
{
    public string Expected { get; } = expected;
    public string Actual { get; } = actual;
}
