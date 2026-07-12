using Schemorph.Core.Ledger;

namespace Schemorph.Core.Providers;

/// <summary>
/// The provider boundary (ADR-0003): inspect, compare, execute, dialect knowledge.
/// Deliberately minimal and behavioral — it specifies what a provider does,
/// never how schemas are modeled internally. Provider-raw results are expressed
/// in Schemorph's own terms; engine types (e.g. DacFx) must not leak through.
/// </summary>
public interface IDatabaseProvider
{
    /// <summary>Stable provider id, e.g. "sqlserver".</summary>
    string Name { get; }

    /// <summary>Read a live database into desired-state SQL files.</summary>
    Task<InspectResult> InspectAsync(InspectRequest request, CancellationToken cancellationToken = default);

    /// <summary>Compare desired state against a live database → raw structural changes.</summary>
    Task<CompareResult> CompareAsync(CompareRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Apply desired state to the target database. <paramref name="includeChange"/>
    /// is the core's policy hook: the provider mechanically applies exactly the
    /// changes the core includes (destructive gating, self-exclusion, ...).
    /// <paramref name="onChangesComputed"/> fires with the full computed change set
    /// before anything executes — from the same comparison that then applies, so a
    /// plan shown through it cannot race a second comparison.
    /// </summary>
    Task<ApplyResult> ApplyAsync(
        ApplyRequest request,
        Func<RawChange, bool> includeChange,
        Action<IReadOnlyList<RawChange>>? onChangesComputed = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Execute a SQL script (dialect knowledge: batch separators, transactions).
    /// Runs all batches in one transaction where the database allows.
    /// </summary>
    Task ExecuteScriptAsync(string connectionString, string script, CancellationToken cancellationToken = default);

    /// <summary>
    /// Execute a SQL script and record the given ledger entries in the SAME
    /// transaction (ADR-0004): either the script ran and is recorded, or neither
    /// happened. The caller must have initialized the ledger beforehand.
    /// </summary>
    Task ExecuteScriptAsync(string connectionString, string script, IReadOnlyList<LedgerEntry> ledgerEntries, CancellationToken cancellationToken = default);

    /// <summary>
    /// Analyze the desired state's programmable objects (ADR-0002 strategy 2):
    /// which objects exist, which file defines each, an idempotent apply script
    /// (dialect knowledge: CREATE OR ALTER), and dependencies within the set.
    /// Ordering and checksum policy stay in the core.
    /// </summary>
    Task<ProgrammableAnalysis> AnalyzeProgrammablesAsync(string desiredStateDirectory, CancellationToken cancellationToken = default);
}

public sealed record InspectRequest(string ConnectionString, string OutputDirectory);

public sealed record InspectResult(IReadOnlyList<string> WrittenFiles);

public sealed record CompareRequest(string DesiredStateDirectory, string ConnectionString);

/// <summary>Provider-raw comparison output, in Schemorph terms (no engine types).</summary>
public sealed record CompareResult(
    IReadOnlyList<RawChange> Changes,
    IReadOnlyList<RawMessage> Messages,
    string? UpdateScript);

public sealed record RawChange(string Operation, string ObjectType, string ObjectName);

public sealed record RawMessage(string Severity, string Code, string Text);

/// <summary>One desired-state programmable object, in Schemorph terms.</summary>
/// <param name="ApplyScript">Idempotent re-definition script (e.g. CREATE OR ALTER rewrite of the file).</param>
/// <param name="DependsOn">Names of other programmable objects this one references.</param>
public sealed record ProgrammableObjectInfo(
    string ObjectName,
    string ObjectType,
    string FilePath,
    string ApplyScript,
    IReadOnlyList<string> DependsOn);

public sealed record ProgrammableAnalysis(
    IReadOnlyList<ProgrammableObjectInfo> Objects,
    IReadOnlyList<RawMessage> Messages);

public sealed record ApplyRequest(string DesiredStateDirectory, string ConnectionString);

public sealed record ApplyResult(
    bool Success,
    IReadOnlyList<RawChange> AppliedChanges,
    IReadOnlyList<RawChange> ExcludedChanges,
    IReadOnlyList<RawMessage> Messages);
