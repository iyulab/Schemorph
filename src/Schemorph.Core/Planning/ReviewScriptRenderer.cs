using System.Text;

namespace Schemorph.Core.Planning;

/// <summary>
/// Renders a plan as one SQL document a person can read, sign and archive.
///
/// Production consumers gate schema changes on a human reading the DDL. The tool
/// computes exactly that text and used to keep it: the first such consumer parsed
/// <c>diff --format json</c> and reassembled the SQL themselves, which makes the
/// reviewed text a *different artifact* from the executed one. So this renderer
/// emits the provider's scripts **verbatim** — the declarative update script as
/// generated, each re-definition as it will run — and never re-assembles anything
/// from the plan's per-change explanation slices.
///
/// Review-only by design. The header says so, and it points at
/// <c>apply --expect-plan</c> rather than at a SQL client: running this text
/// directly would skip the ledger, the re-definition ordering and the migration
/// run-once contract. What the reviewer signs and what the apply gate enforces are
/// the same fingerprint, printed at the top.
/// </summary>
public static class ReviewScriptRenderer
{
    /// <summary>
    /// The plan has changes to publish but no script to show for them. Emitting a
    /// partial document would be worse than emitting none: an approval artifact
    /// that silently omits a stage is signed for changes nobody read.
    /// </summary>
    public sealed class ScriptUnavailableException(string message) : Exception(message);

    /// <summary>
    /// Why the script is missing is the provider's to say, not this renderer's to
    /// assert: diagnostic codes are provider-specific, so naming one here states a
    /// cause that may never have fired while hiding the one that did. The plan
    /// carries whatever was actually reported, so the codes present are echoed and
    /// the reader is sent to them — the same correction SCHEMORPH008 needed, where
    /// asserting a cause was itself the misdirection.
    /// </summary>
    private static string ReportedReason(Plan plan)
    {
        var codes = plan.Messages
            .Where(m => m.Severity is "Error" or "Warning")
            .Select(m => m.Code)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return codes.Count == 0
            ? "The plan reports no diagnostic explaining the absence. "
            : $"The plan reports: {string.Join(", ", codes)}. ";
    }

    /// <param name="target">Connection string; redacted before it is written.</param>
    /// <param name="generatedAt">Injected so the output is reproducible under test.</param>
    public static string Render(Plan plan, string target, DateTimeOffset generatedAt)
    {
        // The executed declarative text is read from the plan — the same field the
        // fingerprint binds — so the reviewed text and the hashed text are one artifact
        // by construction, not by the caller passing a match. What actually runs is a
        // subset of it: the engine writes the script over the whole comparison, and the
        // plan can drop objects from execution while their statements stay in the text.
        // Plan.Excluded names them, and the header renders that above the script.
        var updateScript = plan.UpdateScript;
        var declarative = plan.Actions.Where(a => a.Operation != PlanOperation.Redefine).ToList();
        var redefines = plan.Actions.Where(a => a.Operation == PlanOperation.Redefine).ToList();

        // Y6 — honesty over completeness-shaped output.
        if (declarative.Count > 0 && string.IsNullOrWhiteSpace(updateScript))
        {
            throw new ScriptUnavailableException(
                $"The plan has {declarative.Count} declarative change(s) but no update script was " +
                "produced for them, so a review document would omit them. " +
                ReportedReason(plan) +
                "Review the JSON plan instead, or re-run once the cause is resolved.");
        }

        var hash = PlanFingerprint.Compute(plan);
        var sb = new StringBuilder();

        sb.AppendLine("/* ---------------------------------------------------------------");
        sb.AppendLine(" * Schemorph review script — READ ONLY. Do not execute this file.");
        sb.AppendLine(" *");
        sb.AppendLine($" * planHash:  {hash}");
        sb.AppendLine($" * generated: {generatedAt.ToUniversalTime():yyyy-MM-dd HH:mm:ss} UTC");
        sb.AppendLine($" * target:    {Redaction.Redact(target)}");
        sb.AppendLine($" * atomicity: {AtomicityLine(plan)}");
        sb.AppendLine(" *");
        sb.AppendLine($" * {Summary(declarative.Count, redefines.Count)}");
        sb.AppendLine(" *");
        sb.AppendLine(" * Executing this text with a SQL client would skip the history ledger,");
        sb.AppendLine(" * the re-definition ordering and the migration run-once contract. Apply it");
        sb.AppendLine(" * with the tool, gated on the fingerprint above — the apply refuses to");
        sb.AppendLine(" * deviate from the plan this document renders:");
        sb.AppendLine(" *");
        sb.AppendLine($" *   schemorph apply --schema <dir> --expect-plan {hash}");
        sb.AppendLine(" *");
        sb.AppendLine(" * Migrations are not part of this document: they are run-once scripts");
        sb.AppendLine(" * reviewed as files in the repository, not regenerated per plan.");
        sb.AppendLine(" * --------------------------------------------------------------- */");

        // Ahead of the messages, because it is about this document rather than about
        // the plan: it tells the reader that something they are about to read is not
        // part of what runs. Without it the only honest reading of a statement in a
        // review document is that it executes — and that reading would be wrong.
        if (plan.Excluded.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("/* NOT EXECUTED — statements below exist for these objects and will not run.");
            sb.AppendLine(" * The script is the engine's own text, reproduced exactly so that what is");
            sb.AppendLine(" * reviewed and what is applied stay one artifact. That fidelity is why it");
            sb.AppendLine(" * can contain work this plan does not do. Each object here is announced in");
            sb.AppendLine(" * the script by a PRINT naming it.");
            sb.AppendLine(" *");
            foreach (var e in plan.Excluded)
            {
                sb.AppendLine($" *   {Redaction.Redact(e.ObjectName)}");
                sb.AppendLine($" *     {Redaction.Redact(e.Reason)}");
            }
            sb.AppendLine(" *");
            sb.AppendLine(" * Anything NOT listed here does run. A DROP you cannot find above is a");
            sb.AppendLine(" * DROP that executes.");
            sb.AppendLine(" */");
        }

        if (plan.Messages.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("/* Plan messages (warnings never change the exit code):");
            foreach (var m in plan.Messages)
            {
                sb.AppendLine($" *   [{m.Severity}] {m.Code}: {Redaction.Redact(m.Text)}");
            }
            sb.AppendLine(" */");
        }

        // Stage order is execution order (ADR-0002): declarative publish, then
        // re-definitions. A reviewer reads it in the order it will happen.
        if (declarative.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"-- ===== Stage 1 of 2: declarative publish ({declarative.Count} change(s)) =====");
            sb.AppendLine("-- One transaction: it commits in full or not at all.");
            foreach (var a in declarative)
            {
                sb.AppendLine($"--   {a.Operation,-7} {a.ObjectType,-12} {a.ObjectName}{RiskNote(a)}");
            }
            sb.AppendLine();
            sb.AppendLine(updateScript!.TrimEnd());
        }

        if (redefines.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"-- ===== Stage {(declarative.Count > 0 ? 2 : 1)} of 2: idempotent re-definitions " +
                          $"({redefines.Count} object(s)) =====");
            sb.AppendLine("-- One transaction per object, in dependency order; each records its own ledger row.");
            foreach (var a in redefines)
            {
                sb.AppendLine();
                sb.AppendLine($"-- ---- {a.ObjectType} {a.ObjectName}");
                sb.AppendLine(a.Sql?.TrimEnd()
                    ?? $"-- (no script available for {a.ObjectName})");
            }
        }

        if (declarative.Count == 0 && redefines.Count == 0)
        {
            sb.AppendLine();
            sb.AppendLine("-- No changes. The database already matches the desired state.");
        }

        return sb.ToString();
    }

    // The signer should know what the apply guarantees on failure without
    // knowing which engine runs it (ADR-0004 addendum, plan format 1.3).
    private static string AtomicityLine(Plan plan) => plan.Atomicity switch
    {
        Core.Providers.ApplyAtomicity.Transactional =>
            "transactional (the apply lands whole or not at all — docs/failure-semantics.md)",
        _ => "partial (stages commit independently on failure — docs/failure-semantics.md)",
    };

    private static string Summary(int declarative, int redefines) => (declarative, redefines) switch
    {
        (0, 0) => "Nothing to apply.",
        _ => $"{declarative} declarative change(s), {redefines} re-definition(s).",
    };

    private static string RiskNote(PlanAction a) => a.Risk switch
    {
        RiskLevel.Destructive => "   <-- DESTRUCTIVE",
        RiskLevel.Warning => "   <-- review",
        _ => "",
    };
}
