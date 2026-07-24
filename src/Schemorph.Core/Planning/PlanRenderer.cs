using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Schemorph.Core.Planning;

/// <summary>
/// Renders a plan. The JSON form is the primary contract (versioned via
/// <see cref="Plan.FormatVersion"/>); the text form is a rendering of it,
/// never the other way around (design principle §3).
/// </summary>
public static class PlanRenderer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        WriteIndented = true,
    };

    /// <summary>
    /// The single serializable shape of a plan (docs/plan-format.md; the version is
    /// <see cref="Plan.CurrentFormatVersion"/>) — every surface that embeds a plan
    /// (diff output, apply envelope) serializes THIS model, so the contract cannot
    /// drift between commands. Each change
    /// carries an <c>actions</c> *list* (Terraform's convention): today every list
    /// has one verb, but composite operations (e.g. a rebuild = drop + create)
    /// become expressible without a breaking change.
    /// </summary>
    public static object ToJsonModel(Plan plan) => new
    {
        plan.FormatVersion,
        PlanHash = PlanFingerprint.Compute(plan),   // pass to apply --expect-plan / MCP apply
        plan.Atomicity,                             // the apply guarantee (ADR-0004 addendum); not part of the hash
        plan.HasChanges,
        plan.HasDestructiveChanges,
        Changes = plan.Actions.Select(a => new
        {
            a.ObjectName,
            a.ObjectType,
            Actions = new[] { a.Operation },
            a.Risk,
            a.Sql,
            a.Explanation,
        }).ToList(),
        plan.Messages,
    };

    public static string ToJson(Plan plan) => JsonSerializer.Serialize(ToJsonModel(plan), JsonOptions);

    public static string ToText(Plan plan)
    {
        var sb = new StringBuilder();
        if (!plan.HasChanges)
        {
            // With gated-out messages present, "matches" would be a false claim.
            sb.AppendLine(plan.Messages.Count == 0
                ? "No changes. Database matches the desired state."
                : "No applicable changes (see messages below).");
        }
        else
        {
            sb.AppendLine($"Plan: {plan.Actions.Count} change(s)");
            foreach (var action in plan.Actions)
            {
                sb.AppendLine($"  {Marker(action.Risk)} {action.Operation,-8} {action.ObjectType,-12} {action.ObjectName}");
            }
            // The gate token: apply exactly this reviewed plan or refuse.
            sb.AppendLine($"  (apply this exact plan with --expect-plan {PlanFingerprint.Compute(plan)})");
        }

        foreach (var message in plan.Messages)
        {
            sb.AppendLine($"  [{message.Severity}] {message.Code}: {message.Text}");
        }

        return sb.ToString().TrimEnd() + Environment.NewLine;
    }

    private static string Marker(RiskLevel risk) => risk switch
    {
        RiskLevel.Safe => "+",
        RiskLevel.Warning => "~",
        RiskLevel.Destructive => "!",
        _ => "?",
    };
}
