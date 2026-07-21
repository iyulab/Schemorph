using Schemorph.Core.Ledger;
using Schemorph.Core.Providers;

namespace Schemorph.Core.Migrations;

/// <summary>
/// Versioned-migration semantics (strategy 3): ordered, checksummed, run once.
/// The runner owns discovery, ordering, tamper detection and ledger bookkeeping;
/// the provider only executes scripts (mechanism).
/// </summary>
public sealed class MigrationRunner(IDatabaseProvider provider, ILedgerStore ledger)
{
    public const string LedgerKind = "migration";

    /// <summary>
    /// Discovery + ledger consultation, without executing anything (read-only):
    /// what would run, what already ran, what was ignored. Duplicate versions and
    /// tampered applied migrations throw here — a plan over invalid inputs is not
    /// a plan.
    /// </summary>
    public async Task<MigrationPlan> PlanAsync(
        string migrationsDirectory, string connectionString, CancellationToken cancellationToken = default)
    {
        // 1. Discover and order.
        var scripts = new List<MigrationScript>();
        var ignored = new List<string>();
        foreach (var file in Directory.GetFiles(migrationsDirectory, "*.sql", SearchOption.TopDirectoryOnly))
        {
            if (MigrationScript.TryParse(file, out var script)) scripts.Add(script!);
            else ignored.Add(Path.GetFileName(file));
        }
        scripts.Sort((a, b) => a.Version.CompareTo(b.Version));

        var duplicate = scripts.GroupBy(s => s.Version).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            throw new MigrationException(
                $"Duplicate migration version V{duplicate.Key}: {string.Join(", ", duplicate.Select(s => s.FileName))}");
        }

        // 2. Consult the ledger; validate ALL applied checksums before running anything.
        var appliedEntries = (await ledger.ReadAsync(connectionString, LedgerKind, cancellationToken))
            .Where(e => e.Succeeded)
            .ToDictionary(e => e.ObjectName, e => e, StringComparer.OrdinalIgnoreCase);

        foreach (var script in scripts.Where(s => appliedEntries.ContainsKey(s.FileName)))
        {
            var recorded = appliedEntries[script.FileName].Checksum;
            if (!string.Equals(recorded, script.Checksum, StringComparison.OrdinalIgnoreCase))
            {
                throw new MigrationException(
                    $"Migration {script.FileName} was modified after being applied " +
                    $"(ledger checksum {recorded}, file checksum {script.Checksum}). " +
                    "Applied migrations are immutable; add a new migration instead.");
            }
        }

        var pending = scripts.Where(s => !appliedEntries.ContainsKey(s.FileName)).ToList();

        // 3. Safety lint over what would run (SCHEMORPH1xx band, like the plan
        //    lint): dialect judgment from the provider, codes/wording here.
        //    Applied migrations are history — only pending ones are worth a warning.
        var warnings = new List<RawMessage>();
        foreach (var script in pending)
        {
            var signals = await provider.LintMigrationScriptAsync(script.Text, cancellationToken);
            warnings.AddRange(signals.Select(signal => Warn(script.FileName, signal)));
        }

        return new MigrationPlan(
            pending,
            scripts.Count(s => appliedEntries.ContainsKey(s.FileName)),
            ignored,
            warnings);
    }

    private static RawMessage Warn(string fileName, MigrationLintSignal signal) => signal switch
    {
        MigrationLintSignal.Truncate => new RawMessage("Warning", "SCHEMORPH104",
            $"{fileName}: TRUNCATE TABLE — removes every row, minimally logged and not selectively recoverable."),
        MigrationLintSignal.UnfilteredUpdate => new RawMessage("Warning", "SCHEMORPH105",
            $"{fileName}: UPDATE without a WHERE clause rewrites every row. Add a filter, or a guard proving the intent."),
        MigrationLintSignal.UnfilteredDelete => new RawMessage("Warning", "SCHEMORPH105",
            $"{fileName}: DELETE without a WHERE clause removes every row. Add a filter, or a guard proving the intent."),
        MigrationLintSignal.PermissionChange => new RawMessage("Warning", "SCHEMORPH106",
            $"{fileName}: GRANT/REVOKE/DENY — permission changes riding a migration; consider managing security separately."),
        _ => new RawMessage("Warning", "SCHEMORPH106", $"{fileName}: {signal}"),
    };

    public async Task<MigrationRunResult> RunAsync(
        string migrationsDirectory, string connectionString, CancellationToken cancellationToken = default)
    {
        var plan = await PlanAsync(migrationsDirectory, connectionString, cancellationToken);

        // Run pending migrations in order. The ledger row commits in the SAME
        // transaction as the script (ADR-0004): a crash can never leave a migration
        // applied but unrecorded, which would re-run it and break run-once.
        var applied = new List<string>();
        foreach (var script in plan.Pending)
        {
            var entry = new LedgerEntry(LedgerKind, script.FileName, "Run", script.Checksum, Succeeded: true, Detail: null);
            try
            {
                // The discovery snapshot runs — the same text the checksum covers.
                await provider.ExecuteScriptAsync(
                    connectionString, script.Text, new[] { entry }, cancellationToken);
            }
            catch (Exception ex)
            {
                await ledger.AppendFailureBestEffortAsync(
                    connectionString, entry with { Succeeded = false, Detail = ex.Message }, cancellationToken);
                // Same reasoning as the redefine stage: the run-once contract makes
                // "which ones already ran" the operator's first question, and only
                // this frame knows the answer.
                throw new MigrationExecutionException(script.FileName, applied.ToList(), ex);
            }
            applied.Add(script.FileName);
        }

        return new MigrationRunResult(applied, plan.AppliedCount, plan.IgnoredFiles, plan.Warnings);
    }
}

/// <summary>Read-only judgment: pending scripts (in run order), applied count, ignored files, lint warnings on pending scripts.</summary>
public sealed record MigrationPlan(
    IReadOnlyList<MigrationScript> Pending,
    int AppliedCount,
    IReadOnlyList<string> IgnoredFiles,
    IReadOnlyList<RawMessage> Warnings);

public sealed record MigrationRunResult(
    IReadOnlyList<string> Applied,
    int Skipped,
    IReadOnlyList<string> IgnoredFiles,
    IReadOnlyList<RawMessage> Warnings);

public sealed class MigrationException(string message) : Exception(message);

/// <summary>
/// A migration script failed against the database. Distinct from
/// <see cref="MigrationException"/>, which reports a desired-state problem found
/// before anything runs (duplicate versions, an edited applied migration): this
/// is an execution failure, and it names the script that failed plus the ones
/// that had already run.
/// </summary>
public sealed class MigrationExecutionException(
    string fileName, IReadOnlyList<string> applied, Exception inner)
    : Exception($"Migration {fileName} failed: {inner.Message}", inner)
{
    /// <summary>The script that failed.</summary>
    public string FileName { get; } = fileName;

    /// <summary>Scripts that ran (and were recorded) before the failure — committed.</summary>
    public IReadOnlyList<string> Applied { get; } = applied;
}
