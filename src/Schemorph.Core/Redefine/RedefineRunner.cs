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

    /// <summary>
    /// Splits the desired state into pending re-definitions and brownfield
    /// reconciliations, in dependency order. An object with no ledger history is
    /// not assumed pending: if its live definition already matches the file
    /// (provider judgment), it is *reconcilable* — apply records its checksum
    /// without executing anything, so adopting an existing database never
    /// redefines what already matches (ADR-0002 addendum). Read-only.
    /// </summary>
    public async Task<RedefinePlan> PlanAsync(
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

        var candidates = ordered
            .Where(o => !lastChecksum.TryGetValue(o.ObjectName, out var recorded)
                        || !string.Equals(recorded, ChecksumOf(o), StringComparison.OrdinalIgnoreCase))
            .ToList();

        // Only history-less objects can reconcile; a recorded-but-different
        // checksum means the *files* moved on and must be re-applied. The live
        // lookup is skipped entirely when everything has history (steady state).
        var unknown = candidates.Where(o => !lastChecksum.ContainsKey(o.ObjectName)).ToList();
        var reconcilable = unknown.Count == 0
            ? Array.Empty<ProgrammableObjectInfo>()
            : await provider.FilterMatchingLiveDefinitionsAsync(connectionString, unknown, cancellationToken);
        var reconcilableNames = reconcilable.Select(o => o.ObjectName).ToHashSet(StringComparer.OrdinalIgnoreCase);

        return new RedefinePlan(
            candidates.Where(o => !reconcilableNames.Contains(o.ObjectName))
                .Select(o => new PendingRedefine(o, lastChecksum.ContainsKey(o.ObjectName)
                    ? RedefineReason.ChecksumChanged
                    : RedefineReason.NoHistory))
                .ToList(),
            candidates.Where(o => reconcilableNames.Contains(o.ObjectName)).ToList());
    }

    /// <summary>
    /// Record reconciliations, then apply every pending re-definition — each
    /// recorded in the ledger.
    /// </summary>
    public async Task<RedefineRunResult> RunAsync(
        ProgrammableAnalysis analysis, string connectionString, CancellationToken cancellationToken = default)
        => await RunAsync(analysis,
            await PlanAsync(analysis, connectionString, cancellationToken), connectionString, cancellationToken);

    /// <summary>
    /// Execute a plan already computed by <see cref="PlanAsync"/> — the apply
    /// gate depends on this: what was shown (and fingerprinted) is exactly what
    /// runs, never a silent re-plan.
    /// </summary>
    public async Task<RedefineRunResult> RunAsync(
        ProgrammableAnalysis analysis, RedefinePlan plan, string connectionString, CancellationToken cancellationToken = default)
    {

        // Reconciliation is bookkeeping, not change: the checksum lands in the
        // ledger so the object has history from here on, but nothing executes.
        if (plan.Reconcilable.Count > 0)
        {
            await ledger.AppendAsync(connectionString, plan.Reconcilable
                .Select(o => new LedgerEntry(LedgerKind, o.ObjectName, "Reconcile", ChecksumOf(o),
                    Succeeded: true, Detail: o.ObjectType))
                .ToList(), cancellationToken);
        }

        var redefined = new List<string>();
        foreach (var obj in plan.Pending.Select(p => p.Object))
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

        var reconciled = plan.Reconcilable.Select(o => o.ObjectName).ToList();
        return new RedefineRunResult(redefined, analysis.Objects.Count - redefined.Count - reconciled.Count, reconciled);
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

    // The checksum judges the loaded snapshot (never a re-read), so the ledger
    // records exactly the content whose apply script ran.
    private static string ChecksumOf(ProgrammableObjectInfo obj) =>
        ContentChecksum.Compute(obj.FileText);

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

/// <summary>Why a programmable object is pending re-definition.</summary>
public enum RedefineReason
{
    /// <summary>No ledger history and the live definition does not match the file.</summary>
    NoHistory,
    /// <summary>The file's checksum differs from the last successfully applied one.</summary>
    ChecksumChanged,
}

/// <summary>
/// One pending re-definition and why. The strategy renders its own plan action
/// (plan explanations): the SQL is the exact idempotent script that will run,
/// the explanation states the checksum judgment behind the decision.
/// </summary>
public sealed record PendingRedefine(ProgrammableObjectInfo Object, RedefineReason Reason)
{
    public PlanAction ToPlanAction() => new(
        Object.ObjectName, Object.ObjectType, PlanOperation.Redefine, RiskLevel.Safe,
        Sql: Object.ApplyScript,
        Explanation: Reason switch
        {
            RedefineReason.ChecksumChanged =>
                "The file's checksum differs from the last applied definition; re-defined idempotently (CREATE OR ALTER).",
            _ =>
                "No history in the ledger and the live definition does not match the file; defined idempotently (CREATE OR ALTER) and recorded.",
        });
}

/// <summary>The runner's read-only judgment: what to re-apply, what to merely record.</summary>
public sealed record RedefinePlan(
    IReadOnlyList<PendingRedefine> Pending,
    IReadOnlyList<ProgrammableObjectInfo> Reconcilable);

public sealed record RedefineRunResult(
    IReadOnlyList<string> Redefined, int Skipped, IReadOnlyList<string> Reconciled);

public sealed class RedefineException(string message) : Exception(message);
