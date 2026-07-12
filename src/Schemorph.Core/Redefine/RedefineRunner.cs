using Schemorph.Core.Ledger;
using Schemorph.Core.Planning;
using Schemorph.Core.Providers;

namespace Schemorph.Core.Redefine;

/// <summary>
/// Idempotent re-definition semantics (ADR-0002 strategy 2): a programmable
/// object is (re)applied via its provider-supplied apply script when its file's
/// checksum differs from the last ledger record, in dependency order. The runner
/// owns ordering, checksum policy and ledger bookkeeping; the provider supplies
/// analysis (objects, dependencies, dialect rewrite) and script execution.
/// </summary>
public sealed class RedefineRunner(IDatabaseProvider provider, ILedgerStore ledger)
{
    public const string LedgerKind = "redefine";

    /// <summary>Objects whose current file differs from the last applied state, in dependency order.</summary>
    public async Task<IReadOnlyList<ProgrammableObjectInfo>> PlanAsync(
        ProgrammableAnalysis analysis, string connectionString, CancellationToken cancellationToken = default)
    {
        var ordered = TopologicalOrder(analysis.Objects);

        // Latest successful entry per object wins; a drop tombstone (null checksum)
        // never matches a file, so re-added objects are always pending.
        var lastChecksum = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in await ledger.ReadAsync(connectionString, LedgerKind, cancellationToken))
        {
            if (entry.Succeeded) lastChecksum[entry.ObjectName] = entry.Checksum;
        }

        return ordered
            .Where(o => !lastChecksum.TryGetValue(o.ObjectName, out var recorded)
                        || !string.Equals(recorded, ChecksumOf(o), StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    /// <summary>Apply every pending re-definition and record each in the ledger.</summary>
    public async Task<RedefineRunResult> RunAsync(
        ProgrammableAnalysis analysis, string connectionString, CancellationToken cancellationToken = default)
    {
        var pending = await PlanAsync(analysis, connectionString, cancellationToken);
        var redefined = new List<string>();
        foreach (var obj in pending)
        {
            // Ledger row commits in the same transaction as the script (ADR-0004).
            var entry = new LedgerEntry(LedgerKind, obj.ObjectName, "Redefine", ChecksumOf(obj),
                Succeeded: true, Detail: obj.ObjectType);
            try
            {
                await provider.ExecuteScriptAsync(connectionString, obj.ApplyScript, new[] { entry }, cancellationToken);
            }
            catch (Exception ex)
            {
                await ledger.AppendFailureBestEffortAsync(
                    connectionString, entry with { Succeeded = false, Detail = ex.Message }, cancellationToken);
                throw;
            }
            redefined.Add(obj.ObjectName);
        }

        return new RedefineRunResult(redefined, analysis.Objects.Count - redefined.Count);
    }

    /// <summary>
    /// Record drop tombstones for programmable objects the declarative path removed,
    /// so this strategy's history never claims a dropped object is still applied.
    /// </summary>
    public Task RecordDropsAsync(
        string connectionString, IEnumerable<RawChange> appliedChanges, CancellationToken cancellationToken = default)
    {
        var tombstones = appliedChanges
            .Where(c => ProgrammableObjects.IsProgrammable(c.ObjectType)
                        && PlanBuilder.Classify(c).Operation == PlanOperation.Drop)
            .Select(c => new LedgerEntry(LedgerKind, c.ObjectName, "Drop", Checksum: null,
                Succeeded: true, Detail: c.ObjectType))
            .ToList();
        return tombstones.Count == 0
            ? Task.CompletedTask
            : ledger.AppendAsync(connectionString, tombstones, cancellationToken);
    }

    private static string ChecksumOf(ProgrammableObjectInfo obj) =>
        ContentChecksum.Compute(File.ReadAllText(obj.FilePath));

    /// <summary>Kahn topological sort, stable (alphabetical among ready nodes).</summary>
    private static List<ProgrammableObjectInfo> TopologicalOrder(IReadOnlyList<ProgrammableObjectInfo> objects)
    {
        var byName = objects.ToDictionary(o => o.ObjectName, StringComparer.OrdinalIgnoreCase);
        var remaining = objects.ToDictionary(
            o => o.ObjectName,
            o => o.DependsOn.Where(byName.ContainsKey)   // deps outside the set (e.g. tables) don't order us
                .ToHashSet(StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);

        var order = new List<ProgrammableObjectInfo>();
        while (remaining.Count > 0)
        {
            var ready = remaining.Where(kv => kv.Value.Count == 0).Select(kv => kv.Key)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
            if (ready.Count == 0)
            {
                throw new RedefineException(
                    "Dependency cycle among programmable objects: " + string.Join(", ", remaining.Keys.Order()));
            }
            foreach (var name in ready)
            {
                order.Add(byName[name]);
                remaining.Remove(name);
                foreach (var deps in remaining.Values) deps.Remove(name);
            }
        }
        return order;
    }
}

public sealed record RedefineRunResult(IReadOnlyList<string> Redefined, int Skipped);

public sealed class RedefineException(string message) : Exception(message);
