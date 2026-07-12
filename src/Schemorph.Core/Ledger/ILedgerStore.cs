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

/// <summary>One applied change, as recorded in the ledger.</summary>
/// <param name="Kind">declarative | redefine | migration</param>
public sealed record LedgerEntry(
    string Kind,
    string ObjectName,
    string Operation,
    string? Checksum,
    bool Succeeded,
    string? Detail);

/// <summary>Names of Schemorph's own database objects, excluded from comparison.</summary>
public static class LedgerObjects
{
    public const string HistoryTable = "__SchemorphHistory";

    public static bool IsLedgerObject(string objectName) =>
        objectName.Contains(HistoryTable, StringComparison.OrdinalIgnoreCase);
}
