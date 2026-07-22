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

    /// <summary>
    /// The declared surface: capability lines plus the apply-atomicity
    /// guarantee (ADR-0004 addendum). The canonical source for the CLI
    /// manifest's provider block and the plan envelope's <c>atomicity</c>
    /// field; anything absent from it must refuse with
    /// <see cref="UnsupportedByProviderException"/>.
    /// </summary>
    ProviderCapabilities Capabilities { get; }

    /// <summary>
    /// Read a live database into rendered desired-state files (relative path +
    /// content, conventional kind layout). Rendering only — the caller chooses
    /// the sink (disk via InspectOperation, MCP resource, ...).
    /// </summary>
    Task<InspectResult> InspectAsync(InspectRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Load and classify a desired-state directory (dialect knowledge: what is a
    /// model file vs a deploy script / seed DML). Loaded ONCE per operation and
    /// passed back into compare/apply/analyze, so a single operation never
    /// re-reads or re-parses the directory.
    /// </summary>
    Task<IDesiredState> LoadDesiredStateAsync(string desiredStateDirectory, CancellationToken cancellationToken = default);

    /// <summary>Compare desired state against a live database → raw structural changes.</summary>
    Task<CompareResult> CompareAsync(CompareRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Apply desired state to the target database. <paramref name="includeChange"/>
    /// is the core's policy hook: the provider mechanically applies exactly the
    /// changes the core includes (destructive gating, self-exclusion, ...).
    /// <paramref name="onChangesComputed"/> fires before anything executes, with
    /// the comparison the apply will carry out — from the same session, so a plan
    /// shown through it cannot race a second comparison. It carries the compare
    /// shape rather than a bare change list because the plan built from it must
    /// account for everything the apply will do, including the re-definitions a
    /// column change invalidates. (It does not carry the update script: DacFx
    /// treats generating the script and publishing as alternative actions on one
    /// comparison — see the apply-path attribution item in ROADMAP.)
    /// </summary>
    Task<ApplyResult> ApplyAsync(
        ApplyRequest request,
        Func<RawChange, bool> includeChange,
        Action<CompareResult>? onChangesComputed = null,
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
    Task<ProgrammableAnalysis> AnalyzeProgrammablesAsync(IDesiredState desiredState, CancellationToken cancellationToken = default);

    /// <summary>
    /// Of the given desired-state programmable objects, the subset whose live
    /// database definition already matches its file — dialect knowledge: where
    /// definitions are stored, batch separators, idempotent-syntax equivalence
    /// (CREATE OR ALTER vs CREATE). Brownfield reconciliation (ADR-0002 addendum):
    /// lets the core adopt a database it did not build without redefining what
    /// already matches. Objects absent from the database never match; when in
    /// doubt, a provider must report no match (the safe fallback is one
    /// idempotent redefinition).
    /// </summary>
    Task<IReadOnlyList<ProgrammableObjectInfo>> FilterMatchingLiveDefinitionsAsync(
        string connectionString, IReadOnlyList<ProgrammableObjectInfo> objects, CancellationToken cancellationToken = default);

    /// <summary>
    /// Safety-lint dialect judgment on a migration script: which risky
    /// constructs it provably contains. Conservative by contract — a script
    /// that cannot be parsed yields no signals (a missing warning is honest,
    /// a wrong one is not). Policy (codes, messages, gating) stays in the core.
    /// </summary>
    Task<IReadOnlyList<MigrationLintSignal>> LintMigrationScriptAsync(
        string scriptText, CancellationToken cancellationToken = default);
}

/// <summary>Provably-present risky constructs in a migration script (dialect judgment).</summary>
public enum MigrationLintSignal
{
    /// <summary>TRUNCATE TABLE — bulk, minimally-logged, not row-recoverable.</summary>
    Truncate,
    /// <summary>UPDATE without a WHERE clause — rewrites every row.</summary>
    UnfilteredUpdate,
    /// <summary>DELETE without a WHERE clause — removes every row.</summary>
    UnfilteredDelete,
    /// <summary>GRANT / REVOKE / DENY — permission changes riding a migration.</summary>
    PermissionChange,
}

/// <summary>
/// A desired-state directory, loaded and classified once by its provider
/// (<see cref="IDatabaseProvider.LoadDesiredStateAsync"/>) and passed back into
/// compare/apply/analyze — one load (file I/O + classification parse) serves
/// every step of an operation. Opaque above the boundary: only the
/// classification outcome is visible, never how the provider models the files.
/// A state with <see cref="Errors"/> must not be passed onward; callers fail
/// their operation first (providers throw if one slips through).
/// </summary>
public interface IDesiredState
{
    /// <summary>
    /// Classification skip warnings (deploy scripts, seed DML) — the caller
    /// surfaces them exactly once per operation, on the plan/apply messages.
    /// </summary>
    IReadOnlyList<RawMessage> Warnings { get; }

    /// <summary>Load/classification errors (e.g. an unparseable model file), file-attributed.</summary>
    IReadOnlyList<RawMessage> Errors { get; }
}

public sealed record InspectRequest(string ConnectionString);

/// <summary>One rendered desired-state file, e.g. ("tables/dbo.Orders.sql", "CREATE TABLE ...").</summary>
public sealed record DesiredStateFile(string RelativePath, string Content)
{
    // A FIXED set — deliberately not Path.GetInvalidFileNameChars(), which is
    // platform-dependent (on Linux it is only '/' and NUL). A desired-state
    // tree is checked out across operating systems, so the same database must
    // render the same file names everywhere; the strictest common denominator
    // is the Windows set.
    private static readonly char[] InvalidChars =
        { '<', '>', ':', '"', '/', '\\', '|', '?', '*' };

    /// <summary>
    /// An object name as a usable file-name segment. Database identifiers admit
    /// characters no file system does (<c>we"ird</c>, <c>a/b</c>, quoted
    /// mixed-case with separators), and every provider that renders inspect
    /// output hits the same wall — so the rule lives here once. Offending
    /// characters become <c>_</c>, and a sanitized name carries a short content
    /// hash of the original so two different identifiers can never collapse
    /// into the same file. Names that are already clean pass through verbatim.
    /// </summary>
    public static string SafeSegment(string objectName)
    {
        var sanitized = new string(objectName
            .Select(c => Array.IndexOf(InvalidChars, c) >= 0 || char.IsControl(c) ? '_' : c)
            .ToArray())
            .TrimEnd(' ', '.');

        return sanitized == objectName
            ? objectName
            : $"{(sanitized.Length == 0 ? "object" : sanitized)}-{ContentChecksum.Compute(objectName)[..8]}";
    }
}

public sealed record InspectResult(IReadOnlyList<DesiredStateFile> Files);

public sealed record CompareRequest(IDesiredState DesiredState, string ConnectionString);

/// <summary>
/// Provider-raw comparison output, in Schemorph terms (no engine types).
/// <paramref name="TablesWithColumnChanges"/>: tables where an existing column's
/// definition changes (type, nullability, collation) — not columns added or
/// dropped. A programmable object's text can be byte-identical across such a
/// change while its meaning is not, so this is what strategy 2 needs in order to
/// invalidate what depends on it (see <see cref="ProgrammableObjectInfo.DependsOnTables"/>).
/// </summary>
public sealed record CompareResult(
    IReadOnlyList<RawChange> Changes,
    IReadOnlyList<RawMessage> Messages,
    string? UpdateScript,
    IReadOnlyList<ChangeScript>? ChangeScripts = null,
    IReadOnlyList<string>? TablesWithColumnChanges = null);

/// <summary>
/// The slice of the update script attributable to one change (plan explanations):
/// descriptive only — what executes is always the whole update script. Absent
/// whenever attribution is not certain; a missing slice is honest, a wrong one
/// is not. <paramref name="Rebuild"/>: the engine cannot alter in place and
/// rebuilds the object (new table, rows copied, old dropped, renamed).
/// <paramref name="AddsNotNullWithoutDefault"/>: the slice adds a NOT NULL
/// column with no default — fails on any table that already holds rows
/// (dialect judgment; false whenever it cannot be proven).
/// </summary>
public sealed record ChangeScript(
    string ObjectName, string Sql, bool Rebuild, bool AddsNotNullWithoutDefault = false);

public sealed record RawChange(string Operation, string ObjectType, string ObjectName);

public sealed record RawMessage(string Severity, string Code, string Text);

/// <summary>One desired-state programmable object, in Schemorph terms.</summary>
/// <param name="FileText">
/// The defining file's text at load time. Checksums and live-definition
/// matching judge THIS snapshot — the one the apply script was derived from —
/// so what is recorded is always what actually ran, never a later re-read.
/// </param>
/// <param name="ApplyScript">Idempotent re-definition script (e.g. CREATE OR ALTER rewrite of the file).</param>
/// <param name="DependsOn">Names of other programmable objects this one references.</param>
/// <param name="DependsOnTables">
/// Tables this object reads, directly or through a referenced column. Its own
/// text says nothing about their column types, yet its meaning depends on them —
/// which is why a column change there must invalidate this object even when its
/// file has not moved.
/// </param>
public sealed record ProgrammableObjectInfo(
    string ObjectName,
    string ObjectType,
    string FilePath,
    string FileText,
    string ApplyScript,
    IReadOnlyList<string> DependsOn,
    IReadOnlyList<string>? DependsOnTables = null);

public sealed record ProgrammableAnalysis(
    IReadOnlyList<ProgrammableObjectInfo> Objects,
    IReadOnlyList<RawMessage> Messages);

public sealed record ApplyRequest(IDesiredState DesiredState, string ConnectionString);

public sealed record ApplyResult(
    bool Success,
    IReadOnlyList<RawChange> AppliedChanges,
    IReadOnlyList<RawChange> ExcludedChanges,
    IReadOnlyList<RawMessage> Messages);
