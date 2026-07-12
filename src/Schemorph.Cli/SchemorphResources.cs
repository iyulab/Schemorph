using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Schemorph.Core;
using Schemorph.Core.Operations;
using Schemorph.Core.Planning;
using Schemorph.Core.Providers;
using Schemorph.Provider.SqlServer;

namespace Schemorph.Cli;

/// <summary>
/// Schema-as-context (ROADMAP Phase 2): the current schema state and the
/// current plan as MCP *resources*, so hosts can attach them as context
/// instead of round-tripping tool calls. Same core operations as the tools —
/// resources are a third rendering of the same API, never a wrapper.
///
/// Configuration follows the tools' env-confinement pattern: the database
/// comes from SCHEMORPH_URL, and the plan resource's desired-state directory
/// from SCHEMORPH_SCHEMA_DIR — neither ever flows through the conversation.
/// Errors surface as MCP protocol errors (resources have no envelope channel),
/// with the same redaction as every other sink.
/// </summary>
[McpServerResourceType]
internal sealed class SchemorphResources
{
    [McpServerResource(UriTemplate = "schemorph://schema", Name = "Live database schema", MimeType = "application/sql")]
    [Description("The live database's current schema as desired-state SQL (every object, grouped by kind, " +
                 "the exact rendering `inspect` writes to disk). The database comes from SCHEMORPH_URL " +
                 "in the server environment.")]
    public static async Task<string> Schema(CancellationToken cancellationToken)
    {
        var files = await RenderSchemaAsync(cancellationToken);
        var text = new StringBuilder();
        foreach (var file in files)
        {
            text.AppendLine($"-- {file.RelativePath}");
            text.AppendLine(file.Content.TrimEnd());
            text.AppendLine();
        }
        return text.ToString();
    }

    [McpServerResource(UriTemplate = "schemorph://schema/{kind}/{name}", Name = "Live schema object")]
    [Description("One live database object as desired-state SQL, e.g. schemorph://schema/tables/dbo.Orders. " +
                 "Kinds: tables, views, procedures, functions, triggers. The database comes from " +
                 "SCHEMORPH_URL in the server environment.")]
    public static async Task<ResourceContents> SchemaObject(
        [AllowedValues("tables", "views", "procedures", "functions", "triggers")] string kind,
        string name,
        CancellationToken cancellationToken)
    {
        var relativePath = $"{kind}/{name}.sql";
        var files = await RenderSchemaAsync(cancellationToken);
        var file = files.FirstOrDefault(f => string.Equals(f.RelativePath, relativePath, StringComparison.OrdinalIgnoreCase))
            ?? throw new McpException($"No {kind} object named '{name}' in the live database. " +
                                      "Read schemorph://schema to see every object.");
        return new TextResourceContents
        {
            Uri = $"schemorph://schema/{kind}/{name}",
            MimeType = "application/sql",
            Text = file.Content,
        };
    }

    [McpServerResource(UriTemplate = "schemorph://plan", Name = "Current schema change plan", MimeType = "application/json")]
    [Description("The plan schemorph_diff would produce right now: desired-state SQL files " +
                 "(SCHEMORPH_SCHEMA_DIR in the server environment) vs the live database (SCHEMORPH_URL). " +
                 "Versioned plan JSON including planHash — usable directly as schemorph_apply's expectedPlanHash.")]
    public static async Task<string> Plan(CancellationToken cancellationToken)
    {
        var url = RequireEnv("SCHEMORPH_URL");
        var schemaDir = RequireEnv("SCHEMORPH_SCHEMA_DIR");
        if (!Directory.Exists(schemaDir))
        {
            throw new McpException($"SCHEMORPH_SCHEMA_DIR does not exist: {schemaDir}. " +
                                   "Point it at the directory holding the desired-state .sql files.");
        }

        // Same semantics as schemorph_diff's default (destructive changes gated),
        // so the resource's planHash is exactly what a reviewed apply expects.
        var result = await DiffOperation.RunAsync(
            new SqlServerProvider(), new SqlServerLedgerStore(), schemaDir, url,
            allowDestructive: false, cancellationToken);
        if (!result.Success)
        {
            throw new McpException(Redaction.Redact(
                string.Join("; ", result.Errors.Select(m => $"{m.Code}: {m.Text}"))));
        }

        return PlanRenderer.ToJson(result.Plan!);
    }

    private static async Task<IReadOnlyList<DesiredStateFile>> RenderSchemaAsync(CancellationToken cancellationToken)
    {
        var url = RequireEnv("SCHEMORPH_URL");
        try
        {
            var inspected = await new SqlServerProvider().InspectAsync(new InspectRequest(url), cancellationToken);
            return inspected.Files;
        }
        catch (Exception ex) when (ex is not McpException and not OperationCanceledException)
        {
            throw new McpException(Redaction.Redact($"Reading the live schema failed: {ex.Message}"));
        }
    }

    private static string RequireEnv(string name) =>
        Environment.GetEnvironmentVariable(name)
        ?? throw new McpException($"{name} is not set in the MCP server's environment. " +
                                  "Configure it on the server entry (e.g. \"env\": {\"" + name + "\": \"...\"}); " +
                                  "it is never passed through the conversation.");
}
