// Schemorph CLI — verb-oriented, structured output first (design principle §3).
// Exit codes are semantic (Terraform convention):
//   0 = success / no changes pending, 1 = error, 2 = changes pending.
// Argument parsing is deliberately hand-rolled while the surface is small;
// a parser library is an open decision deferred until flags force the question.

using System.Text.Json;
using Schemorph.Core.Ledger;
using Schemorph.Core.Migrations;
using Schemorph.Core.Planning;
using Schemorph.Core.Providers;
using Schemorph.Core.Redefine;
using Schemorph.Provider.SqlServer;

const int ExitNoChanges = 0;
const int ExitError = 1;
const int ExitChangesPending = 2;

var verb = args.Length > 0 ? args[0].ToLowerInvariant() : null;
var format = ParseOption(args, "--format") ?? "text";

switch (verb)
{
    case "diff":
        return await RunDiff(args, format);
    case "inspect":
        return await RunInspect(args, format);
    case "apply":
        return await RunApply(args, format);
    case "status":
        return Fail(format, "not_implemented", $"'{verb}' is not implemented yet.",
            "Phase 1 in progress — see the roadmap.");
    case "--version" or "version":
        Console.WriteLine("schemorph 0.0.1-dev");
        return ExitNoChanges;
    default:
        Console.Error.WriteLine("""
            schemorph — declarative, SQL-first schema management

            usage: schemorph <verb> [options]

            verbs:
              inspect   read a live database into desired-state SQL files
              diff      compute the change plan (never applies anything)
              apply     execute the change plan and record it in the history ledger
              status    show drift and ledger state

            inspect options:
              --url <connection-string>   source database (required)
              --out <dir>                 output directory for desired-state files (required)

            diff options:
              --url <connection-string>   target database (required)
              --schema <dir>              desired-state SQL directory (required)
              --allow-destructive         include destructive changes in the plan
              --format json|text          output form (default: text)
            """);
        return verb is null or "help" or "--help" ? ExitNoChanges : ExitError;
}

async Task<int> RunApply(string[] args, string format)
{
    var url = ParseOption(args, "--url");
    var schemaDir = ParseOption(args, "--schema");
    var allowDestructive = args.Contains("--allow-destructive");

    if (url is null || schemaDir is null)
    {
        return Fail(format, "invalid_arguments", "apply requires --url and --schema.",
            "schemorph apply --url \"<connection-string>\" --schema ./schema");
    }
    if (!Directory.Exists(schemaDir))
    {
        return Fail(format, "schema_dir_not_found", $"Schema directory not found: {schemaDir}",
            "Pass the directory that holds the desired-state .sql files.");
    }

    try
    {
        IDatabaseProvider provider = new SqlServerProvider();

        // Strategy 2 analysis runs first so a misplaced file fails before any DB work.
        var programmables = await provider.AnalyzeProgrammablesAsync(schemaDir);
        if (programmables.Messages.Any(m => m.Severity == "Error"))
        {
            foreach (var m in programmables.Messages)
            {
                Console.Error.WriteLine($"[{m.Severity}] {m.Code}: {m.Text}");
            }
            return Fail(format, "invalid_desired_state", "Desired state is invalid.", "See messages above.");
        }

        var result = await provider.ApplyAsync(
            new ApplyRequest(schemaDir, url),
            change => PlanBuilder.ShouldInclude(change, allowDestructive));

        if (!result.Success)
        {
            foreach (var m in result.Messages)
            {
                Console.Error.WriteLine($"[{m.Severity}] {m.Code}: {m.Text}");
            }
            return Fail(format, "apply_failed", "Apply reported errors.", "See messages above.");
        }

        // Every applied change is recorded in the history ledger — the audit trail.
        ILedgerStore ledger = new SqlServerLedgerStore();
        await ledger.EnsureInitializedAsync(url);
        await ledger.AppendAsync(url, result.AppliedChanges
            .Select(c => new LedgerEntry("declarative", c.ObjectName, c.Operation, Checksum: null,
                Succeeded: true, Detail: c.ObjectType))
            .ToList());

        // Strategy 2: idempotent re-definitions run after the declarative publish
        // (structural prerequisites first). Declarative drops leave a tombstone so
        // re-adding an identical file later still re-creates the object.
        var redefineRunner = new RedefineRunner(provider, ledger);
        await redefineRunner.RecordDropsAsync(url, result.AppliedChanges);
        var redefineRun = await redefineRunner.RunAsync(programmables, url);

        // Strategy 3: versioned migrations run after the declarative apply.
        MigrationRunResult? migrationRun = null;
        var migrationsDir = ParseOption(args, "--migrations");
        if (migrationsDir is not null)
        {
            if (!Directory.Exists(migrationsDir))
            {
                return Fail(format, "migrations_dir_not_found", $"Migrations directory not found: {migrationsDir}",
                    "Pass the directory that holds V####__description.sql files.");
            }
            migrationRun = await new MigrationRunner(provider, ledger).RunAsync(migrationsDir, url);
        }

        // Schemorph's own bookkeeping stays invisible in user-facing output, and
        // redefine-routed exclusions are not "excluded" — they show as redefinitions.
        var excludedVisible = result.ExcludedChanges
            .Where(c => !LedgerObjects.IsLedgerObject(c.ObjectName) && !PlanBuilder.RoutesToRedefine(c))
            .ToList();
        var visibleMessages = result.Messages.Where(m => !LedgerObjects.IsLedgerObject(m.Text)).ToList();

        if (format == "json")
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                applied = result.AppliedChanges,
                excluded = excludedVisible,
                messages = visibleMessages,
                redefines = new
                {
                    applied = redefineRun.Redefined,
                    skipped = redefineRun.Skipped,
                },
                migrations = migrationRun is null ? null : new
                {
                    applied = migrationRun.Applied,
                    skipped = migrationRun.Skipped,
                    ignoredFiles = migrationRun.IgnoredFiles,
                },
            }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true }));
        }
        else
        {
            Console.WriteLine($"Applied {result.AppliedChanges.Count} change(s); excluded {excludedVisible.Count}.");
            foreach (var c in result.AppliedChanges) Console.WriteLine($"  applied  {c.Operation,-8} {c.ObjectType,-12} {c.ObjectName}");
            foreach (var c in excludedVisible) Console.WriteLine($"  excluded {c.Operation,-8} {c.ObjectType,-12} {c.ObjectName}");
            foreach (var m in visibleMessages) Console.WriteLine($"  [{m.Severity}] {m.Code}: {m.Text}");
            Console.WriteLine($"Redefined {redefineRun.Redefined.Count} programmable object(s); {redefineRun.Skipped} unchanged.");
            foreach (var name in redefineRun.Redefined) Console.WriteLine($"  redefined {name}");
            if (migrationRun is not null)
            {
                Console.WriteLine($"Migrations: {migrationRun.Applied.Count} applied, {migrationRun.Skipped} already applied.");
                foreach (var name in migrationRun.Applied) Console.WriteLine($"  migrated {name}");
                foreach (var name in migrationRun.IgnoredFiles) Console.WriteLine($"  ignored  {name} (name does not match V####__description.sql)");
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

async Task<int> RunInspect(string[] args, string format)
{
    var url = ParseOption(args, "--url");
    var outDir = ParseOption(args, "--out");

    if (url is null || outDir is null)
    {
        return Fail(format, "invalid_arguments", "inspect requires --url and --out.",
            "schemorph inspect --url \"<connection-string>\" --out ./schema");
    }

    try
    {
        IDatabaseProvider provider = new SqlServerProvider();
        var result = await provider.InspectAsync(new InspectRequest(url, outDir));

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
    var url = ParseOption(args, "--url");
    var schemaDir = ParseOption(args, "--schema");
    var allowDestructive = args.Contains("--allow-destructive");

    if (url is null || schemaDir is null)
    {
        return Fail(format, "invalid_arguments", "diff requires --url and --schema.",
            "schemorph diff --url \"<connection-string>\" --schema ./schema");
    }
    if (!Directory.Exists(schemaDir))
    {
        return Fail(format, "schema_dir_not_found", $"Schema directory not found: {schemaDir}",
            "Pass the directory that holds the desired-state .sql files.");
    }

    try
    {
        IDatabaseProvider provider = new SqlServerProvider();
        var compared = await provider.CompareAsync(new CompareRequest(schemaDir, url));

        if (compared.Messages.Any(m => m.Severity == "Error"))
        {
            foreach (var m in compared.Messages)
            {
                Console.Error.WriteLine($"[{m.Severity}] {m.Code}: {m.Text}");
            }
            return Fail(format, "compare_failed", "Comparison reported errors.", "See messages above.");
        }

        // Strategy 2: pending idempotent re-definitions join the plan (read-only —
        // checksums against the ledger; a missing ledger table reads as no history).
        var programmables = await provider.AnalyzeProgrammablesAsync(schemaDir);
        if (programmables.Messages.Any(m => m.Severity == "Error"))
        {
            foreach (var m in programmables.Messages)
            {
                Console.Error.WriteLine($"[{m.Severity}] {m.Code}: {m.Text}");
            }
            return Fail(format, "invalid_desired_state", "Desired state is invalid.", "See messages above.");
        }
        var pendingRedefines = await new RedefineRunner(provider, new SqlServerLedgerStore())
            .PlanAsync(programmables, url);

        var plan = PlanBuilder.Build(compared, allowDestructive, pendingRedefines);
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

static int Fail(string format, string kind, string message, string hint)
{
    if (format == "json")
    {
        Console.Error.WriteLine(JsonSerializer.Serialize(new { error = new { kind, message, hint } }));
    }
    else
    {
        Console.Error.WriteLine($"error: {message} ({hint})");
    }
    return ExitError;
}
