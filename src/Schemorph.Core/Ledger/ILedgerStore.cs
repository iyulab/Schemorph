namespace Schemorph.Core.Ledger;

/// <summary>
/// The history ledger: single audit trail for all Schemorph activity against a
/// database, across all three strategies. The core owns the ledger's semantics
/// and shape; providers contribute only a dialect implementation of this
/// contract (ADR-0003: the provider boundary itself knows nothing of it).
/// </summary>
public interface ILedgerStore
{
    /// <summary>Create the ledger table if it does not exist.</summary>
    Task EnsureInitializedAsync(string connectionString, CancellationToken cancellationToken = default);

    /// <summary>Append entries for changes that were applied.</summary>
    Task AppendAsync(string connectionString, IReadOnlyList<LedgerEntry> entries, CancellationToken cancellationToken = default);

    /// <summary>Read all entries of one kind (e.g. "migration"), oldest first.</summary>
    Task<IReadOnlyList<LedgerEntry>> ReadAsync(string connectionString, string kind, CancellationToken cancellationToken = default);
}

public static class LedgerStoreExtensions
{
    /// <summary>
    /// Record a failure row (ADR-0004 decision 4). Best effort by design: a ledger
    /// that cannot be written to must never mask the error being recorded. Detail
    /// text is redacted here — the ledger persists, so this sink must never store
    /// credential material that an error message happened to embed.
    /// </summary>
    public static async Task AppendFailureBestEffortAsync(
        this ILedgerStore ledger, string connectionString, LedgerEntry failure, CancellationToken cancellationToken = default)
    {
        if (failure.Detail is not null)
        {
            failure = failure with { Detail = Redaction.Redact(failure.Detail) };
        }
        try
        {
            await ledger.AppendAsync(connectionString, new[] { failure }, cancellationToken);
        }
        catch
        {
            // Swallowed deliberately; the caller rethrows the original error.
        }
    }
}

/// <summary>One applied change, as recorded in the ledger.</summary>
/// <param name="Kind">declarative | redefine | migration</param>
/// <param name="AppliedAtUtc">Set by the store on read (the database stamps it on insert); null on entries being written.</param>
public sealed record LedgerEntry(
    string Kind,
    string ObjectName,
    string Operation,
    string? Checksum,
    bool Succeeded,
    string? Detail,
    DateTime? AppliedAtUtc = null);

/// <summary>Names of Schemorph's own database objects, excluded from comparison.</summary>
public static class LedgerObjects
{
    public const string HistoryTable = "__SchemorphHistory";

    public static bool IsLedgerObject(string objectName) =>
        objectName.Contains(HistoryTable, StringComparison.OrdinalIgnoreCase);
}
