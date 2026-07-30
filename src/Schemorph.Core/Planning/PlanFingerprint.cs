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
/// null, so this is what closes the hole for every provider.
///
/// Still excluded, deliberately: messages (diagnostics, not execution),
/// <c>explanation</c> (prose about a change, not the change), and
/// <c>atomicity</c> (a static provider property). Reviewing a diff and applying
/// its hash guarantees the apply runs the reviewed text or refuses (no
/// diff-apply race). The hash is stable across runs because its inputs are —
/// the update script is generated deterministically from the same comparison
/// the diff showed.
///
/// All of that rests on one property of the encoding: **different plans must
/// produce different input strings.** Everything hashed here is either an
/// identifier or SQL text, so the delimiters must be characters neither can
/// contain. Ordinary punctuation will not do — SQL text is routinely multi-line
/// and may hold any printable character, so a newline or a pipe lets the
/// *content* decide where one field ends and the next begins, and two different
/// plans can reach the same string that way. The C0 separators cannot occur in
/// either input, so the boundaries come from the encoding instead: <c>US</c>
/// between an action's members, <c>RS</c> between actions and before the script.
/// They are written as escapes on purpose; a literal control character in the
/// source is invisible to every reader of it.
/// </summary>
public static class PlanFingerprint
{
    /// <summary>U+001F UNIT SEPARATOR — between the members of one action.</summary>
    private const char Field = '\u001F';

    /// <summary>U+001E RECORD SEPARATOR — between actions, and before the executed script.</summary>
    private const char Record = '\u001E';

    public static string Compute(Plan plan)
    {
        var shape = string.Join(Record, plan.Actions.Select(action => string.Join(Field,
            action.ObjectName,
            action.ObjectType,
            action.Operation.ToString(),
            action.Risk.ToString(),
            action.Sql)));
        return ContentChecksum.Compute($"{shape}{Record}{plan.UpdateScript}");
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
