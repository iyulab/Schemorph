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

    public static string ToJson(Plan plan) => JsonSerializer.Serialize(new
    {
        plan.FormatVersion,
        plan.HasChanges,
        plan.HasDestructiveChanges,
        plan.Actions,
        plan.Messages,
    }, JsonOptions);

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
