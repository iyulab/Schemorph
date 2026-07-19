// Schemorph CLI — verb-oriented, structured output first (design principle §3).
// Exit codes are semantic (Terraform convention):
//   0 = success / no changes pending, 1 = error, 2 = changes pending.
// Argument parsing is deliberately hand-rolled while the surface is small;
// a parser library is an open decision deferred until flags force the question.

using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Schemorph.Cli;
using Schemorph.Core;
using Schemorph.Core.Errors;
using Schemorph.Core.Ledger;
using Schemorph.Core.Migrations;
using Schemorph.Core.Operations;
using Schemorph.Core.Planning;
using Schemorph.Core.Providers;
using Schemorph.Core.Redefine;

const int ExitNoChanges = (int)ExitCode.Success;
const int ExitError = (int)ExitCode.Error;
const int ExitChangesPending = (int)ExitCode.ChangesPending;

var verb = args.Length > 0 ? args[0].ToLowerInvariant() : null;
// Agent-first default: a redirected stdout is a machine consumer, so JSON is the
// default there; a terminal gets text. --format always wins over the heuristic.
var format = ParseOption(args, "--format") ?? (Console.IsOutputRedirected ? "json" : "text");

switch (verb)
{
    case "diff":
        return await RunDiff(args, format);
    case "inspect":
        return await RunInspect(args, format);
    case "apply":
        return await RunApply(args, format);
    case "schema":
        Console.WriteLine(CliManifest.ToJson(InformationalVersion()));
        return ExitNoChanges;
    case "mcp":
        return await RunMcp();
    case "status":
        return await RunStatus(args, format);
    case "--version" or "version":
        Console.WriteLine($"schemorph {InformationalVersion()}");
        return ExitNoChanges;
    default:
        // Requested help (bare invocation, help, --help) is the primary output
        // and belongs on stdout; only usage-on-error goes to stderr.
        var requested = verb is null or "help" or "--help";
        (requested ? Console.Out : Console.Error).WriteLine("""
            schemorph — declarative, SQL-first schema management

            usage: schemorph <verb> [options]

            verbs:
              inspect   read a live database into desired-state SQL files
              diff      compute the change plan (never applies anything)
              apply     execute the change plan and record it in the history ledger
              schema    print a JSON manifest of this CLI (verbs, options, exit codes)
              mcp       run as an MCP server over stdio: tools + schema/plan
                        resources (set SCHEMORPH_URL — and SCHEMORPH_SCHEMA_DIR
                        for the plan resource — in the server environment)
              status    show drift, ledger summary, and pending migrations
              version   print the tool version

            status options:
              --url <connection-string>   target database (required)
              --schema <dir>              desired-state SQL directory (required)
              --migrations <dir>          also report pending migration scripts
              --format json|text          output form (default: text on a terminal,
                                          json when stdout is redirected)

            inspect options:
              --url <connection-string>   source database (required)
              --out <dir>                 output directory for desired-state files (required)

            diff options:
              --url <connection-string>   target database (required)
              --schema <dir>              desired-state SQL directory (required)
              --allow-destructive         include destructive changes in the plan
              --format json|text          output form (default: text on a terminal,
                                          json when stdout is redirected)

            apply options:
              --url <connection-string>   target database (required)
              --schema <dir>              desired-state SQL directory (required)
              --migrations <dir>          versioned migration scripts (V####__description.sql)
              --allow-destructive         apply destructive changes too
              --expect-plan <hash>        apply only if the computed plan matches this
                                          fingerprint (printed by diff); mismatch = abort
              --format json|text          output form (default: text on a terminal,
                                          json when stdout is redirected)

            connection:
              --url can be omitted when the SCHEMORPH_URL environment variable is set
              (preferred: keeps credentials out of shell history). Passwords are
              redacted from every output channel.

            Exit codes: 0 = success / no changes, 1 = error, 2 = diff found pending changes.
            Errors are a typed envelope on stderr (docs/errors.md).
            """);
        return requested ? ExitNoChanges : ExitError;
}

// MCP server over stdio (ROADMAP Phase 2): read-only/plan-only tools, apply
// behind the plan-fingerprint gate, and schema/plan state as resources.
// stdout is the protocol channel, so all logging is forced to stderr.
async Task<int> RunMcp()
{
    var builder = Host.CreateApplicationBuilder();
    builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
    builder.Services.AddMcpServer(o => o.ServerInfo = new ModelContextProtocol.Protocol.Implementation
        {
            Name = "schemorph",
            Version = InformationalVersion(),   // one version string everywhere (CLI --version parity)
        })
        .WithStdioServerTransport()
        .WithTools<SchemorphTools>()
        .WithResources<SchemorphResources>();
    await builder.Build().RunAsync();
    return ExitNoChanges;
}

async Task<int> RunApply(string[] args, string format)
{
    var url = ResolveUrl(args);
    var schemaDir = ParseOption(args, "--schema");
    var allowDestructive = args.Contains("--allow-destructive");

    if (url is null || schemaDir is null)
    {
        return Fail(format, "invalid_arguments", "apply requires --url (or SCHEMORPH_URL) and --schema.",
            "schemorph apply --url \"<connection-string>\" --schema ./schema");
    }
    if (!Directory.Exists(schemaDir))
    {
        return Fail(format, "schema_dir_not_found", $"Schema directory not found: {schemaDir}",
            "Pass the directory that holds the desired-state .sql files.");
    }

    var migrationsDir = ParseOption(args, "--migrations");
    if (migrationsDir is not null && !Directory.Exists(migrationsDir))
    {
        return Fail(format, "migrations_dir_not_found", $"Migrations directory not found: {migrationsDir}",
            "Pass the directory that holds V####__description.sql files.");
    }

    try
    {
        // The apply itself lives in the core (ApplyOperation) — the CLI and the
        // MCP surface render the same operation; this method renders and maps errors.
        var (provider, ledger) = ProviderSelection.Current;
        var outcome = await ApplyOperation.RunAsync(
            provider, ledger,
            new ApplyOperation.Request(schemaDir, url, allowDestructive, migrationsDir,
                ParseOption(args, "--expect-plan")),
            onPlan: plan =>
            {
                if (format != "json")
                {
                    Console.Write(PlanRenderer.ToText(plan));
                    Console.WriteLine();
                }
            });

        if (!outcome.Success)
        {
            foreach (var m in outcome.Errors) EchoMessage(m);
            return outcome.Stage switch
            {
                ApplyOperation.FailureStage.DesiredState =>
                    Fail(format, "invalid_desired_state", "Desired state is invalid.", "See messages above."),
                ApplyOperation.FailureStage.PlanMismatch =>
                    Fail(format, "plan_mismatch", outcome.Errors[0].Text,
                        "Re-run diff, review the new plan, and pass its hash with --expect-plan."),
                _ => Fail(format, "apply_failed", "Apply reported errors.", "See messages above."),
            };
        }

        var redefineRun = outcome.Redefines!;
        var migrationRun = outcome.Migrations;
        if (format == "json")
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                // One serialization of the plan contract everywhere (docs/plan-format.md).
                plan = outcome.Plan is null ? null : PlanRenderer.ToJsonModel(outcome.Plan),
                applied = outcome.Applied,
                excluded = outcome.ExcludedVisible,
                messages = outcome.VisibleMessages,
                redefines = new
                {
                    applied = redefineRun.Redefined,
                    skipped = redefineRun.Skipped,
                    reconciled = redefineRun.Reconciled,
                },
                migrations = migrationRun is null ? null : new
                {
                    applied = migrationRun.Applied,
                    skipped = migrationRun.Skipped,
                    ignoredFiles = migrationRun.IgnoredFiles,
                    warnings = migrationRun.Warnings,
                },
            }, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
                WriteIndented = true,
            }));
        }
        else
        {
            Console.WriteLine($"Applied {outcome.Applied.Count} change(s); excluded {outcome.ExcludedVisible.Count}.");
            foreach (var c in outcome.Applied) Console.WriteLine($"  applied  {c.Operation,-8} {c.ObjectType,-12} {c.ObjectName}");
            foreach (var c in outcome.ExcludedVisible) Console.WriteLine($"  excluded {c.Operation,-8} {c.ObjectType,-12} {c.ObjectName}");
            foreach (var m in outcome.VisibleMessages) EchoMessage(m, toError: false);
            Console.WriteLine($"Redefined {redefineRun.Redefined.Count} programmable object(s); {redefineRun.Skipped} unchanged.");
            foreach (var name in redefineRun.Redefined) Console.WriteLine($"  redefined {name}");
            if (redefineRun.Reconciled.Count > 0)
            {
                Console.WriteLine($"Reconciled {redefineRun.Reconciled.Count} existing object(s) already matching their files (recorded, nothing executed).");
                foreach (var name in redefineRun.Reconciled) Console.WriteLine($"  reconciled {name}");
            }
            if (migrationRun is not null)
            {
                Console.WriteLine($"Migrations: {migrationRun.Applied.Count} applied, {migrationRun.Skipped} already applied.");
                foreach (var name in migrationRun.Applied) Console.WriteLine($"  migrated {name}");
                foreach (var name in migrationRun.IgnoredFiles) Console.WriteLine($"  ignored  {name} (name does not match V####__description.sql)");
                foreach (var w in migrationRun.Warnings) Console.WriteLine($"  [{w.Severity}] {w.Code}: {w.Text}");
            }
        }
        return ExitNoChanges;
    }
    catch (MigrationException ex)
    {
        return Fail(format, "migration_failed", ex.Message,
            "Applied migrations are immutable; add a new V####__*.sql instead of editing old ones.");
    }
    catch (RedefineException ex)
    {
        return Fail(format, "redefine_failed", ex.Message,
            "Break the cycle by extracting the shared logic into a separate object.");
    }
    catch (Exception ex)
    {
        return Fail(format, "apply_failed", ex.Message, "Verify the connection string and schema directory.");
    }
}

async Task<int> RunStatus(string[] args, string format)
{
    var url = ResolveUrl(args);
    var schemaDir = ParseOption(args, "--schema");

    if (url is null || schemaDir is null)
    {
        return Fail(format, "invalid_arguments", "status requires --url (or SCHEMORPH_URL) and --schema.",
            "schemorph status --url \"<connection-string>\" --schema ./schema [--migrations ./migrations]");
    }
    if (!Directory.Exists(schemaDir))
    {
        return Fail(format, "schema_dir_not_found", $"Schema directory not found: {schemaDir}",
            "Pass the directory that holds the desired-state .sql files.");
    }
    var migrationsDir = ParseOption(args, "--migrations");
    if (migrationsDir is not null && !Directory.Exists(migrationsDir))
    {
        return Fail(format, "migrations_dir_not_found", $"Migrations directory not found: {migrationsDir}",
            "Pass the directory that holds V####__description.sql files.");
    }

    try
    {
        var (provider, ledger) = ProviderSelection.Current;
        var result = await StatusOperation.RunAsync(
            provider, ledger,
            new StatusOperation.Request(schemaDir, url, migrationsDir));

        if (!result.Success)
        {
            foreach (var m in result.Errors) EchoMessage(m);
            return result.Stage == DiffOperation.FailureStage.DesiredState
                ? Fail(format, "invalid_desired_state", "Desired state is invalid.", "See messages above.")
                : Fail(format, "compare_failed", "Comparison reported errors.", "See messages above.");
        }

        var status = result.Status!;
        if (format == "json")
        {
            Console.WriteLine(JsonSerializer.Serialize(new
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
            }, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
                WriteIndented = true,
            }));
        }
        else
        {
            Console.WriteLine(status.Plan.HasChanges
                ? $"Drift: {status.Plan.Actions.Count} pending change(s)."
                : "No drift. Database matches the desired state.");
            Console.Write(PlanRenderer.ToText(status.Plan));
            var kinds = string.Join(", ", status.Ledger.ByKind.Select(kv => $"{kv.Key} {kv.Value}"));
            var lastActivity = status.Ledger.LastActivityUtc is { } at ? $"{at:yyyy-MM-dd HH:mm:ss}Z" : "never";
            Console.WriteLine($"Ledger: {status.Ledger.TotalEntries} entr(ies) ({kinds}); {status.Ledger.Failures} failure(s); last activity {lastActivity}.");
            if (status.Migrations is { } m)
            {
                Console.WriteLine($"Migrations: {m.PendingFiles.Count} pending, {m.AppliedCount} applied.");
                foreach (var name in m.PendingFiles) Console.WriteLine($"  pending  {name}");
                foreach (var name in m.IgnoredFiles) Console.WriteLine($"  ignored  {name} (name does not match V####__description.sql)");
                foreach (var w in m.Warnings) Console.WriteLine($"  [{w.Severity}] {w.Code}: {w.Text}");
            }
        }
        // Same convention as diff: pending work = exit 2, so scripts/agents can branch.
        return status.HasPendingWork ? ExitChangesPending : ExitNoChanges;
    }
    catch (MigrationException ex)
    {
        return Fail(format, "migration_failed", ex.Message,
            "Applied migrations are immutable; add a new V####__*.sql instead of editing old ones.");
    }
    catch (RedefineException ex)
    {
        return Fail(format, "redefine_failed", ex.Message,
            "Break the cycle by extracting the shared logic into a separate object.");
    }
    catch (Exception ex)
    {
        return Fail(format, "compare_failed", ex.Message, "Verify the connection string and schema directory.");
    }
}

async Task<int> RunInspect(string[] args, string format)
{
    var url = ResolveUrl(args);
    var outDir = ParseOption(args, "--out");

    if (url is null || outDir is null)
    {
        return Fail(format, "invalid_arguments", "inspect requires --url (or SCHEMORPH_URL) and --out.",
            "schemorph inspect --url \"<connection-string>\" --out ./schema");
    }

    try
    {
        var result = await InspectOperation.RunAsync(ProviderSelection.Current.Provider, url, outDir);

        if (format == "json")
        {
            Console.WriteLine(JsonSerializer.Serialize(new { files = result.WrittenFiles }));
        }
        else
        {
            Console.WriteLine($"Wrote {result.WrittenFiles.Count} file(s) to {outDir}");
            foreach (var file in result.WrittenFiles) Console.WriteLine($"  {file}");
        }
        return ExitNoChanges;
    }
    catch (Exception ex)
    {
        return Fail(format, "inspect_failed", ex.Message, "Verify the connection string and output directory.");
    }
}

async Task<int> RunDiff(string[] args, string format)
{
    var url = ResolveUrl(args);
    var schemaDir = ParseOption(args, "--schema");
    var allowDestructive = args.Contains("--allow-destructive");

    if (url is null || schemaDir is null)
    {
        return Fail(format, "invalid_arguments", "diff requires --url (or SCHEMORPH_URL) and --schema.",
            "schemorph diff --url \"<connection-string>\" --schema ./schema");
    }
    if (!Directory.Exists(schemaDir))
    {
        return Fail(format, "schema_dir_not_found", $"Schema directory not found: {schemaDir}",
            "Pass the directory that holds the desired-state .sql files.");
    }

    try
    {
        // The diff itself lives in the core (DiffOperation) so the CLI and the MCP
        // surface render the same operation; this method only renders and maps errors.
        var (provider, ledger) = ProviderSelection.Current;
        var result = await DiffOperation.RunAsync(
            provider, ledger, schemaDir, url, allowDestructive);

        if (!result.Success)
        {
            foreach (var m in result.Errors) EchoMessage(m);
            return result.Stage == DiffOperation.FailureStage.DesiredState
                ? Fail(format, "invalid_desired_state", "Desired state is invalid.", "See messages above.")
                : Fail(format, "compare_failed", "Comparison reported errors.", "See messages above.");
        }

        var plan = result.Plan!;
        Console.WriteLine(format == "json" ? PlanRenderer.ToJson(plan) : PlanRenderer.ToText(plan));
        return plan.HasChanges ? ExitChangesPending : ExitNoChanges;
    }
    catch (RedefineException ex)
    {
        return Fail(format, "redefine_failed", ex.Message,
            "Break the cycle by extracting the shared logic into a separate object.");
    }
    catch (Exception ex)
    {
        return Fail(format, "compare_failed", ex.Message, "Verify the connection string and schema directory.");
    }
}

static string? ParseOption(string[] args, string name)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

static string InformationalVersion()
{
    var version = System.Reflection.Assembly.GetExecutingAssembly()
        .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), inherit: false)
        .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
        .FirstOrDefault()?.InformationalVersion ?? "0.0.0-unknown";
    var plus = version.IndexOf('+');   // strip the source-revision suffix
    return plus < 0 ? version : version[..plus];
}

// Environment variable preferred over --url so the connection string (and its
// credential) stays out of shell history and process listings.
static string? ResolveUrl(string[] args)
    => ParseOption(args, "--url") ?? Environment.GetEnvironmentVariable("SCHEMORPH_URL");

static void EchoMessage(RawMessage m, bool toError = true)
{
    var line = Redaction.Redact($"[{m.Severity}] {m.Code}: {m.Text}");
    if (toError) Console.Error.WriteLine(line); else Console.WriteLine($"  {line}");
}

// The error envelope contract ({kind, code, message, hint}) lives in the core
// (SchemorphError, docs/errors.md); this is only its rendering.
static int Fail(string format, string code, string message, string hint)
{
    var error = SchemorphError.Create(code, Redaction.Redact(message), Redaction.Redact(hint));
    if (format == "json")
    {
        Console.Error.WriteLine(JsonSerializer.Serialize(new { error },
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
    }
    else
    {
        Console.Error.WriteLine($"error[{error.Code}]: {error.Message} ({error.Hint})");
    }
    return (int)ExitCode.Error;
}
