namespace Schemorph.Core.Planning;

/// <summary>
/// The plan's identity for the apply gate (`--expect-plan`, MCP apply): a
/// SHA-256 over exactly what would execute. Two things are hashed together:
/// <list type="number">
/// <item>each change's name, type, operation and risk, in plan order — the
/// plan's shape (and each redefine action's script, its exact executed text);</item>
/// <item>the declarative update script — the DDL the apply actually runs and the
/// reviewer actually reads.</item>
/// </list>
/// The second input is what makes "two plans that execute the same changes ARE
/// the same plan" true rather than merely asserted: the action tuples alone are
/// an object-level summary, so two plans that alter the same objects with the
/// same operations but different DDL (add column X vs. add column Y; a CHECK
/// added vs. only a constraint re-added) collapse to identical tuples. Binding
/// the executed script text tells them apart — otherwise a reviewer could sign
/// one plan's hash and the gate would pass a materially different apply. The
/// executed text differs even when a provider leaves the per-change <c>sql</c>
/// null (Postgres P1), so this is what closes the hole for every provider.
///
/// Still excluded, deliberately: messages (diagnostics, not execution),
/// <c>explanation</c> (prose about a change, not the change), and
/// <c>atomicity</c> (a static provider property). Reviewing a diff and applying
/// its hash guarantees the apply runs the reviewed text or refuses (no
/// diff-apply race). The hash is stable across runs because its inputs are —
/// the update script is generated deterministically from the same comparison
/// the diff showed.
/// </summary>
public static class PlanFingerprint
{
    public static string Compute(Plan plan)
    {
        var shape = string.Join("\n", plan.Actions.Select(a =>
            $"{a.ObjectName}|{a.ObjectType}|{a.Operation}|{a.Risk}|{a.Sql}"));
        // U+001E (record separator) cannot occur in an identifier or SQL text, so
        // the boundary between the plan shape and the executed script is
        // unambiguous — no crafted content can forge one side as the other.
        return ContentChecksum.Compute($"{shape}{plan.UpdateScript}");
    }
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
