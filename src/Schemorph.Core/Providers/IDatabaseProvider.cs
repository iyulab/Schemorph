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
    /// </summary>
    Task<ApplyResult> ApplyAsync(
        ApplyRequest request,
        Func<RawChange, bool> includeChange,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Execute a SQL script (dialect knowledge: batch separators, transactions).
    /// Runs all batches in one transaction where the database allows.
    /// </summary>
    Task ExecuteScriptAsync(string connectionString, string script, CancellationToken cancellationToken = default);
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

public sealed record ApplyRequest(string DesiredStateDirectory, string ConnectionString);

public sealed record ApplyResult(
    bool Success,
    IReadOnlyList<RawChange> AppliedChanges,
    IReadOnlyList<RawChange> ExcludedChanges,
    IReadOnlyList<RawMessage> Messages);
