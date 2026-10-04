using Schemorph.Core.Ledger;
using Schemorph.Core.Providers;

namespace Schemorph.Core.Migrations;

/// <summary>
/// Which run-once stage a <see cref="MigrationRunner"/> serves. The two differ only in where they
/// run in an apply and how the history ledger files them: versioned migrations after the
/// declarative and redefine stages, pre-stage scripts before the declarative stage — for changes a
/// desired-state diff cannot express, such as a rename, that the diff must then see as done.
/// </summary>
/// <param name="LedgerKind">The history ledger's <c>kind</c> for this stage's scripts.</param>
/// <param name="Noun">How a message names one of its scripts.</param>
public sealed record VersionedStage(string LedgerKind, string Noun)
{
    /// <summary>Versioned migrations: run after the declarative and redefine stages.</summary>
    public static VersionedStage Migration { get; } = new("migration", "Migration");

    /// <summary>Pre-stage scripts: run before the declarative stage, so the plan is computed after them.</summary>
    public static VersionedStage Pre { get; } = new("pre", "Pre-stage script");
}

/// <summary>
/// Versioned-script semantics (strategy 3): ordered, checksummed, run once — for
/// <see cref="VersionedStage.Migration"/> and, with the same contract, <see cref="VersionedStage.Pre"/>.
/// The runner owns discovery, ordering, tamper detection and ledger bookkeeping;
/// the provider only executes scripts (mechanism).
/// </summary>
public sealed class MigrationRunner(IDatabaseProvider provider, ILedgerStore ledger, VersionedStage? stage = null)
{
    public const string LedgerKind = "migration";

    /// <summary>The stage this runner serves; <see cref="VersionedStage.Migration"/> unless given.</summary>
    public VersionedStage Stage { get; } = stage ?? VersionedStage.Migration;

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
                $"Duplicate {Stage.Noun.ToLowerInvariant()} version V{duplicate.Key}: {string.Join(", ", duplicate.Select(s => s.FileName))}");
        }

        // 2. Consult the ledger; validate ALL applied checksums before running anything.
        var appliedEntries = (await ledger.ReadAsync(connectionString, Stage.LedgerKind, cancellationToken))
            .Where(e => e.Succeeded)
            .ToDictionary(e => e.ObjectName, e => e, StringComparer.OrdinalIgnoreCase);

        foreach (var script in scripts.Where(s => appliedEntries.ContainsKey(s.FileName)))
        {
            var recorded = appliedEntries[script.FileName].Checksum;
            if (!string.Equals(recorded, script.Checksum, StringComparison.OrdinalIgnoreCase))
            {
                throw new MigrationException(
                    $"{Stage.Noun} {script.FileName} was modified after being applied " +
                    $"(ledger checksum {recorded}, file checksum {script.Checksum}). " +
                    $"Applied {Stage.Noun.ToLowerInvariant()}s are immutable; add a new one instead.");
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

            // A construct that cannot run inside a transaction is fatal, not a
            // warning, when the provider's apply is one transaction it owns
            // (ADR-0004 addendum) — the same reason PgDesiredState refuses
            // CONCURRENTLY in desired-state files, extended here: a migration
            // now executes inside that SAME session (RedefineRunner and
            // MigrationRunner both thread it through), so the constraint is
            // identical; only the file's role differs.
            if (provider.Capabilities.PlanAtomicity == ApplyAtomicity.Transactional
                && signals.Contains(MigrationLintSignal.NonTransactional))
            {
                throw new MigrationException(
                    $"{script.FileName}: contains a construct that cannot run inside a transaction " +
                    "(e.g. CREATE INDEX CONCURRENTLY), and this provider's apply is one transaction it " +
                    "owns (atomicity: transactional) — split it into a migration run outside this tool, " +
                    "or drop CONCURRENTLY if a lock-holding index build is acceptable here.",
                    hint: "This provider's apply is one transaction it owns; split the migration so the " +
                    "non-transactional statement runs on its own, outside this tool, or drop the " +
                    "construct if it isn't actually needed here.");
            }

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
        MigrationLintSignal.NonTransactional => new RawMessage("Warning", "SCHEMORPH109",
            $"{fileName}: contains a construct that cannot run inside a transaction (e.g. CREATE INDEX CONCURRENTLY) — informational here because this provider does not run migrations inside a shared transaction."),
        _ => new RawMessage("Warning", "SCHEMORPH106", $"{fileName}: {signal}"),
    };

    public async Task<MigrationRunResult> RunAsync(
        string migrationsDirectory, string connectionString, IApplySession? session = null, CancellationToken cancellationToken = default)
        => await RunAsync(await PlanAsync(migrationsDirectory, connectionString, cancellationToken), connectionString, session, cancellationToken);

    /// <summary>
    /// Runs <paramref name="plan"/> exactly — the snapshot an apply gated on, not a re-reading of the
    /// directory, so a file edited after the gate cannot run in place of the one that passed it.
    /// </summary>
    public async Task<MigrationRunResult> RunAsync(
        MigrationPlan plan, string connectionString, IApplySession? session = null, CancellationToken cancellationToken = default)
    {

        // Run pending migrations in order. The ledger row commits in the SAME
        // transaction as the script (ADR-0004) — the session's shared one when
        // given, this call's own otherwise — so a crash can never leave a
        // migration applied but unrecorded, which would re-run it and break
        // run-once.
        var applied = new List<string>();
        foreach (var script in plan.Pending)
        {
            var entry = new LedgerEntry(Stage.LedgerKind, script.FileName, "Run", script.Checksum, Succeeded: true, Detail: null);
            try
            {
                // The discovery snapshot runs — the same text the checksum covers.
                await provider.ExecuteScriptAsync(
                    connectionString, script.Text, new[] { entry }, session, cancellationToken);
            }
            catch (Exception ex)
            {
                await ledger.AppendFailureBestEffortAsync(
                    connectionString, entry with { Succeeded = false, Detail = ex.Message }, cancellationToken);
                // Same reasoning as the redefine stage: the run-once contract
                // makes "which ones already ran" the operator's first question,
                // and only this frame knows the answer.
                throw new MigrationExecutionException(script.FileName, applied.ToList(), ex, EngineErrors.Find(provider, ex), Stage);
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
    IReadOnlyList<RawMessage> Warnings)
{
    /// <summary>The pending migrations as a plan carries them — name and checksum, in run order.</summary>
    public IReadOnlyList<Planning.PlanMigration> ForPlan =>
        Pending.Select(s => new Planning.PlanMigration(s.FileName, s.Checksum)).ToList();
}

public sealed record MigrationRunResult(
    IReadOnlyList<string> Applied,
    int Skipped,
    IReadOnlyList<string> IgnoredFiles,
    IReadOnlyList<RawMessage> Warnings);

/// <summary>
/// A desired-state problem found before anything runs — see the three throw
/// sites in <see cref="MigrationRunner.PlanAsync"/>. <see cref="Hint"/> is null
/// for the original two causes (duplicate version, an edited applied
/// migration): the CLI's own fixed wording already covers those. The third
/// cause (a non-transactional construct rejected because the provider's apply
/// is one transaction it owns) needs different advice, so it carries its own.
/// </summary>
public sealed class MigrationException(string message, string? hint = null) : Exception(message)
{
    public string? Hint { get; } = hint;
}

/// <summary>
/// A migration script failed against the database. Distinct from
/// <see cref="MigrationException"/>, which reports a desired-state problem found
/// before anything runs (duplicate versions, an edited applied migration): this
/// is an execution failure, and it names the script that failed plus the ones
/// that had already run.
/// </summary>
public sealed class MigrationExecutionException(
    string fileName, IReadOnlyList<string> applied, Exception inner, EngineError? engine = null, VersionedStage? stage = null)
    : Exception($"{(stage ?? VersionedStage.Migration).Noun} {fileName} failed: {engine?.Describe() ?? inner.Message}", inner)
{
    /// <summary>The stage the failed script belongs to.</summary>
    public VersionedStage Stage { get; } = stage ?? VersionedStage.Migration;

    /// <summary>
    /// The engine error behind the failure, as the provider described it — null when
    /// the failure was not the database's (the message then carries the exception's own).
    /// </summary>
    public EngineError? Engine { get; } = engine;

    /// <summary>The script that failed.</summary>
    public string FileName { get; } = fileName;

    /// <summary>Scripts that ran (and were recorded) before the failure — committed.</summary>
    public IReadOnlyList<string> Applied { get; } = applied;
}
