using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using Schemorph.Core;
using Schemorph.Core.Errors;
using Schemorph.Core.Operations;
using Schemorph.Core.Planning;
using Schemorph.Core.Providers;

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
    private static readonly JsonSerializerOptions ErrorJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        // Optional envelope fields are absent, not null — see Program.Emit.
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

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

        try
        {
            var (provider, ledger) = ProviderSelection.Current;
            var result = await DiffOperation.RunAsync(
                provider, ledger, schemaDir, url, allowDestructive, cancellationToken);
            if (!result.Success)
            {
                var badState = result.Stage == DiffOperation.FailureStage.DesiredState;
                return Error(badState ? "invalid_desired_state" : "compare_failed",
                    string.Join("; ", result.Errors.Select(m => $"{m.Code}: {m.Text}")),
                    badState ? "Fix the desired-state files named in the message." : null);
            }

            return PlanRenderer.ToJson(result.Plan!);
        }
        catch (TemporaryWorkspaceException ex)
        {
            return TempWorkspaceError(ex);
        }
        catch (Exception ex)
        {
            // Without this the exception escapes into the MCP framework's own error
            // shape, and the promise these tools make — one envelope everywhere —
            // is false exactly when it matters.
            return Error("compare_failed", ex.Message, hint: null);
        }
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

        try
        {
            var result = await InspectOperation.RunAsync(ProviderSelection.Current.Provider, url, outDir, cancellationToken);
            return JsonSerializer.Serialize(new { files = result.WrittenFiles }, ErrorJson);
        }
        catch (TemporaryWorkspaceException ex)
        {
            return TempWorkspaceError(ex);
        }
        catch (Exception ex)
        {
            return Error("inspect_failed", ex.Message, hint: null);
        }
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
            var (provider, ledger) = ProviderSelection.Current;
            var result = await StatusOperation.RunAsync(
                provider, ledger,
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
        catch (TemporaryWorkspaceException ex)
        {
            return TempWorkspaceError(ex);
        }
        catch (Schemorph.Core.Migrations.MigrationException ex)
        {
            return Error("migration_failed", ex.Message,
                "Applied migrations are immutable; add a new V####__*.sql instead of editing old ones.");
        }
        catch (Exception ex)
        {
            return Error("compare_failed", ex.Message, hint: null);
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
            var (provider, ledger) = ProviderSelection.Current;
            var outcome = await ApplyOperation.RunAsync(
                provider, ledger,
                new ApplyOperation.Request(schemaDir, url, allowDestructive, migrationsDir, expectedPlanHash),
                cancellationToken: cancellationToken);

            if (!outcome.Success)
            {
                var text = string.Join("; ", outcome.Errors.Select(m => $"{m.Code}: {m.Text}"));
                // Stages that ran after the publish committed carry what they left
                // behind — same envelope the CLI emits, so an agent reads one shape.
                if (outcome.Stage is ApplyOperation.FailureStage.Redefine or ApplyOperation.FailureStage.Migration)
                {
                    var redefine = outcome.Stage == ApplyOperation.FailureStage.Redefine;
                    var committed = new CommittedWork(
                        outcome.Applied.Count,
                        outcome.Redefines?.Redefined.Count ?? 0,
                        outcome.Migrations?.Applied.Count ?? 0);
                    return Error(
                        redefine ? "redefine_execution_failed" : "migration_execution_failed", text,
                        "Re-running is the resume path (apply is convergent); see docs/failure-semantics.md.",
                        redefine ? "redefine" : "migration", committed);
                }

                var code = outcome.Stage switch
                {
                    ApplyOperation.FailureStage.DesiredState => "invalid_desired_state",
                    ApplyOperation.FailureStage.PlanMismatch => "plan_mismatch",
                    _ => "apply_failed",
                };
                return Error(code, text,
                    code switch
                    {
                        "plan_mismatch" => "Re-run schemorph_diff, review the new plan, and retry with its planHash.",
                        "invalid_desired_state" => "Fix the desired-state files named in the message.",
                        // Publish is transactional — nothing committed — but the
                        // cause is the engine's, so it is not guessed at here.
                        _ => null,
                    });
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
        catch (TemporaryWorkspaceException ex)
        {
            return TempWorkspaceError(ex);
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
        catch (Exception ex)
        {
            return Error("apply_failed", ex.Message, hint: null);
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

    /// <summary>
    /// The tool's own scratch directory could not be created — the same answer on
    /// every tool, naming the directory and the variable that moves it. Here the
    /// variable lives in the MCP server entry, not the caller's shell.
    /// </summary>
    private static string TempWorkspaceError(TemporaryWorkspaceException ex) =>
        Error("temp_workspace_unavailable", ex.Message,
            "Set TMP (or TEMP) in the MCP server's environment to a directory that exists and is writable.");

    /// <summary>The CLI's error envelope, verbatim — agents see one error shape everywhere.</summary>
    private static string Error(
        string code, string message, string? hint, string? stage = null, CommittedWork? committed = null) =>
        JsonSerializer.Serialize(
            new { error = SchemorphError.Create(code, Redaction.Redact(message), hint) with
                { Stage = stage, Committed = committed } },
            ErrorJson);
}
