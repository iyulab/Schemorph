using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using Schemorph.Core;
using Schemorph.Core.Errors;
using Schemorph.Core.Operations;
using Schemorph.Core.Planning;
using Schemorph.Core.Providers;
using Schemorph.Provider.SqlServer;

namespace Schemorph.Cli;

/// <summary>
/// The MCP tool surface (`schemorph mcp`, stdio). Same core operations as the
/// CLI verbs — two renderings of one API (architecture.md), never a wrapper
/// around CLI text. Safety model (ROADMAP Phase 2): read-only/plan-only tools
/// only; apply stays behind a future explicit gate.
///
/// The connection string is deliberately NOT a tool parameter: it comes from
/// SCHEMORPH_URL in the server's environment, so credentials never flow through
/// the MCP conversation (same reasoning as the CLI's env-over-flag preference).
/// </summary>
[McpServerToolType]
internal sealed class SchemorphTools
{
    private static readonly JsonSerializerOptions ErrorJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    [McpServerTool(Name = "schemorph_diff", ReadOnly = true, Idempotent = true)]
    [Description("Compute the schema change plan: desired-state SQL files vs the live database. " +
                 "Never applies anything. Returns the versioned plan JSON (docs/plan-format.md): " +
                 "changes[{objectName, objectType, actions[], risk, sql, explanation}], messages[], planHash. " +
                 "The target database comes from SCHEMORPH_URL in the server environment.")]
    public static async Task<string> Diff(
        [Description("Directory holding desired-state .sql files (non-model files are skipped with a warning)")] string schemaDir,
        [Description("Include destructive changes (data-holding DROPs) in the plan")] bool allowDestructive = false,
        CancellationToken cancellationToken = default)
    {
        if (ResolveUrl() is not { } url)
        {
            return MissingUrl();
        }
        if (!Directory.Exists(schemaDir))
        {
            return Error("schema_dir_not_found", $"Schema directory not found: {schemaDir}",
                "Pass the directory that holds the desired-state .sql files.");
        }

        var result = await DiffOperation.RunAsync(
            new SqlServerProvider(), new SqlServerLedgerStore(), schemaDir, url, allowDestructive, cancellationToken);
        if (!result.Success)
        {
            var code = result.Stage == DiffOperation.FailureStage.DesiredState ? "invalid_desired_state" : "compare_failed";
            return Error(code, string.Join("; ", result.Errors.Select(m => $"{m.Code}: {m.Text}")),
                "Fix the desired-state files or verify the connection.");
        }

        return PlanRenderer.ToJson(result.Plan!);
    }

    [McpServerTool(Name = "schemorph_inspect", Idempotent = true)]
    [Description("Read the live database into desired-state SQL files (one file per object, " +
                 "grouped by kind: tables/, views/, procedures/, functions/, triggers/). " +
                 "Read-only against the database; writes files to the given directory. " +
                 "The source database comes from SCHEMORPH_URL in the server environment.")]
    public static async Task<string> Inspect(
        [Description("Output directory for the desired-state files")] string outDir,
        CancellationToken cancellationToken = default)
    {
        if (ResolveUrl() is not { } url)
        {
            return MissingUrl();
        }

        var result = await InspectOperation.RunAsync(new SqlServerProvider(), url, outDir, cancellationToken);
        return JsonSerializer.Serialize(new { files = result.WrittenFiles }, ErrorJson);
    }

    [McpServerTool(Name = "schemorph_status", ReadOnly = true, Idempotent = true)]
    [Description("Show drift (the plan a diff would produce right now, including gated destructive changes), " +
                 "a history-ledger summary (entries by kind, failures, last activity), and — when " +
                 "migrationsDir is given — pending migration scripts. Read-only. " +
                 "The target database comes from SCHEMORPH_URL in the server environment.")]
    public static async Task<string> Status(
        [Description("Directory holding desired-state .sql files")] string schemaDir,
        [Description("Optional directory of versioned migration scripts to report pending ones")] string? migrationsDir = null,
        CancellationToken cancellationToken = default)
    {
        if (ResolveUrl() is not { } url)
        {
            return MissingUrl();
        }
        if (!Directory.Exists(schemaDir))
        {
            return Error("schema_dir_not_found", $"Schema directory not found: {schemaDir}",
                "Pass the directory that holds the desired-state .sql files.");
        }
        if (migrationsDir is not null && !Directory.Exists(migrationsDir))
        {
            return Error("migrations_dir_not_found", $"Migrations directory not found: {migrationsDir}",
                "Pass the directory that holds V####__description.sql files.");
        }

        try
        {
            var result = await StatusOperation.RunAsync(
                new SqlServerProvider(), new SqlServerLedgerStore(),
                new StatusOperation.Request(schemaDir, url, migrationsDir), cancellationToken);
            if (!result.Success)
            {
                var code = result.Stage == DiffOperation.FailureStage.DesiredState ? "invalid_desired_state" : "compare_failed";
                return Error(code, string.Join("; ", result.Errors.Select(m => $"{m.Code}: {m.Text}")),
                    "Fix the desired-state files or verify the connection.");
            }

            var status = result.Status!;
            return JsonSerializer.Serialize(new
            {
                hasPendingWork = status.HasPendingWork,
                plan = PlanRenderer.ToJsonModel(status.Plan),
                ledger = new
                {
                    totalEntries = status.Ledger.TotalEntries,
                    failures = status.Ledger.Failures,
                    byKind = status.Ledger.ByKind,
                    lastActivityUtc = status.Ledger.LastActivityUtc,
                },
                migrations = status.Migrations is null ? null : new
                {
                    pending = status.Migrations.PendingFiles,
                    applied = status.Migrations.AppliedCount,
                    ignoredFiles = status.Migrations.IgnoredFiles,
                    warnings = status.Migrations.Warnings,
                },
            }, ResultJson);
        }
        catch (Schemorph.Core.Migrations.MigrationException ex)
        {
            return Error("migration_failed", ex.Message,
                "Applied migrations are immutable; add a new V####__*.sql instead of editing old ones.");
        }
    }

    [McpServerTool(Name = "schemorph_apply")]
    [Description("Execute a REVIEWED schema change plan against the database. The explicit gate: " +
                 "expectedPlanHash (the planHash from schemorph_diff) is REQUIRED — the apply runs only " +
                 "if the plan computed now matches the reviewed one; any drift aborts before executing " +
                 "(error code plan_mismatch: re-run diff, review, retry with the new hash). " +
                 "Declarative publish → programmable-object redefines → versioned migrations, " +
                 "all recorded in the history ledger. " +
                 "The target database comes from SCHEMORPH_URL in the server environment.")]
    public static async Task<string> Apply(
        [Description("Directory holding desired-state .sql files")] string schemaDir,
        [Description("The planHash from schemorph_diff — the reviewed plan this apply is allowed to execute")] string expectedPlanHash,
        [Description("Apply destructive changes (data-holding DROPs) too")] bool allowDestructive = false,
        [Description("Optional directory of versioned migration scripts (V####__description.sql)")] string? migrationsDir = null,
        CancellationToken cancellationToken = default)
    {
        if (ResolveUrl() is not { } url)
        {
            return MissingUrl();
        }
        if (!Directory.Exists(schemaDir))
        {
            return Error("schema_dir_not_found", $"Schema directory not found: {schemaDir}",
                "Pass the directory that holds the desired-state .sql files.");
        }
        if (migrationsDir is not null && !Directory.Exists(migrationsDir))
        {
            return Error("migrations_dir_not_found", $"Migrations directory not found: {migrationsDir}",
                "Pass the directory that holds V####__description.sql files.");
        }
        if (string.IsNullOrWhiteSpace(expectedPlanHash))
        {
            return Error("invalid_arguments", "expectedPlanHash is required.",
                "Call schemorph_diff first, review the plan, and pass its planHash.");
        }

        try
        {
            var outcome = await ApplyOperation.RunAsync(
                new SqlServerProvider(), new SqlServerLedgerStore(),
                new ApplyOperation.Request(schemaDir, url, allowDestructive, migrationsDir, expectedPlanHash),
                cancellationToken: cancellationToken);

            if (!outcome.Success)
            {
                var code = outcome.Stage switch
                {
                    ApplyOperation.FailureStage.DesiredState => "invalid_desired_state",
                    ApplyOperation.FailureStage.PlanMismatch => "plan_mismatch",
                    _ => "apply_failed",
                };
                return Error(code, string.Join("; ", outcome.Errors.Select(m => $"{m.Code}: {m.Text}")),
                    code == "plan_mismatch"
                        ? "Re-run schemorph_diff, review the new plan, and retry with its planHash."
                        : "Fix the desired-state files or verify the connection.");
            }

            return JsonSerializer.Serialize(new
            {
                plan = outcome.Plan is null ? null : PlanRenderer.ToJsonModel(outcome.Plan),
                applied = outcome.Applied,
                excluded = outcome.ExcludedVisible,
                messages = outcome.VisibleMessages,
                redefines = new
                {
                    applied = outcome.Redefines!.Redefined,
                    skipped = outcome.Redefines.Skipped,
                    reconciled = outcome.Redefines.Reconciled,
                },
                migrations = outcome.Migrations is null ? null : new
                {
                    applied = outcome.Migrations.Applied,
                    skipped = outcome.Migrations.Skipped,
                    ignoredFiles = outcome.Migrations.IgnoredFiles,
                    warnings = outcome.Migrations.Warnings,
                },
            }, ResultJson);
        }
        catch (Schemorph.Core.Migrations.MigrationException ex)
        {
            return Error("migration_failed", ex.Message,
                "Applied migrations are immutable; add a new V####__*.sql instead of editing old ones.");
        }
        catch (Schemorph.Core.Redefine.RedefineException ex)
        {
            return Error("redefine_failed", ex.Message,
                "Break the cycle by extracting the shared logic into a separate object.");
        }
    }

    private static readonly JsonSerializerOptions ResultJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private static string? ResolveUrl() => Environment.GetEnvironmentVariable("SCHEMORPH_URL");

    private static string MissingUrl() => Error("invalid_arguments",
        "SCHEMORPH_URL is not set in the MCP server's environment.",
        "Configure the connection string as an environment variable on the server entry " +
        "(e.g. \"env\": {\"SCHEMORPH_URL\": \"...\"}); it is never passed through the conversation.");

    /// <summary>The CLI's error envelope, verbatim — agents see one error shape everywhere.</summary>
    private static string Error(string code, string message, string hint) =>
        JsonSerializer.Serialize(new { error = SchemorphError.Create(code, Redaction.Redact(message), hint) }, ErrorJson);
}
