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

    public async Task<MigrationRunResult> RunAsync(
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

        // 3. Run pending migrations in order. The ledger row commits in the SAME
        // transaction as the script (ADR-0004): a crash can never leave a migration
        // applied but unrecorded, which would re-run it and break run-once.
        var applied = new List<string>();
        foreach (var script in scripts.Where(s => !appliedEntries.ContainsKey(s.FileName)))
        {
            var entry = new LedgerEntry(LedgerKind, script.FileName, "Run", script.Checksum, Succeeded: true, Detail: null);
            try
            {
                await provider.ExecuteScriptAsync(
                    connectionString, File.ReadAllText(script.FilePath), new[] { entry }, cancellationToken);
            }
            catch (Exception ex)
            {
                await ledger.AppendFailureBestEffortAsync(
                    connectionString, entry with { Succeeded = false, Detail = ex.Message }, cancellationToken);
                throw;
            }
            applied.Add(script.FileName);
        }

        return new MigrationRunResult(applied, scripts.Count - applied.Count, ignored);
    }
}

public sealed record MigrationRunResult(
    IReadOnlyList<string> Applied,
    int Skipped,
    IReadOnlyList<string> IgnoredFiles);

public sealed class MigrationException(string message) : Exception(message);
