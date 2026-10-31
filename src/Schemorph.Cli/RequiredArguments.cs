using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Schemorph.Cli;

/// <summary>
/// A tool call that leaves out a required argument is answered in the tools' own error shape, naming the
/// argument. Left to the SDK, the binding failure is masked into "An error occurred invoking '&lt;tool&gt;'." —
/// the one failure on this surface with no code, no hint and no name, where every other failure is an envelope
/// (docs/errors.md). The check reads the tool's advertised input schema, so what the client was told is required
/// and what is enforced cannot differ.
/// </summary>
internal static class RequiredArguments
{
    public static McpRequestHandler<CallToolRequestParams, CallToolResult> Guard(
        McpRequestHandler<CallToolRequestParams, CallToolResult> next) =>
        async (context, cancellationToken) =>
        {
            var missing = Missing(context);
            if (missing.Count == 0)
            {
                return await next(context, cancellationToken);
            }

            var tool = context.Params?.Name ?? "the tool";
            return SchemorphTools.Error("invalid_arguments",
                $"{tool} requires {(missing.Count == 1 ? "the argument" : "the arguments")} {string.Join(", ", missing)}.",
                $"Pass {string.Join(", ", missing)} — tools/list shows each tool's input schema and which arguments are required.");
        };

    private static List<string> Missing(RequestContext<CallToolRequestParams> context)
    {
        var missing = new List<string>();
        if (context.MatchedPrimitive is not McpServerTool tool
            || !tool.ProtocolTool.InputSchema.TryGetProperty("required", out var required)
            || required.ValueKind != JsonValueKind.Array)
        {
            return missing;
        }

        var given = context.Params?.Arguments;
        foreach (var name in required.EnumerateArray().Select(e => e.GetString()).OfType<string>())
        {
            if (given is null || !given.TryGetValue(name, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                missing.Add(name);
            }
        }
        return missing;
    }
}
