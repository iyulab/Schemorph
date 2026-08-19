using PgSqlParser;
using Schemorph.Core.Providers;

namespace Schemorph.Provider.Postgres;

/// <summary>
/// A desired-state directory, loaded and classified once (the provider
/// boundary's single-load contract). Classification is parse-based, with the
/// real PostgreSQL grammar: a file either belongs to the declared structural
/// slice (tables, columns, constraints, indexes, schemas), is exactly one
/// programmable object (view, function, procedure or trigger — P3, routed to
/// <see cref="PgProgrammables"/> for idempotent re-definition), is imperative
/// content that is not desired state (skipped loudly, the SQL Server
/// convention), or demands something this provider has not earned yet — and
/// that last case REFUSES rather than skips, because a plan that silently
/// ignored a file would claim a sync it cannot see.
/// </summary>
internal sealed class PgDesiredState : IDesiredState
{
    private PgDesiredState(
        IReadOnlyList<string> modelTexts,
        IReadOnlyList<ProgrammableFile> programmableFiles,
        IReadOnlyList<RawMessage> warnings,
        IReadOnlyList<RawMessage> errors)
    {
        ModelTexts = modelTexts;
        ProgrammableFiles = programmableFiles;
        Warnings = warnings;
        Errors = errors;
    }

    /// <summary>One file classified as a single programmable statement (P3).</summary>
    internal sealed record ProgrammableFile(string Path, string Text);

    /// <summary>The model files' texts, in stable (path-ordered) order.</summary>
    public IReadOnlyList<string> ModelTexts { get; }

    /// <summary>
    /// Files classified as exactly one view/function/procedure/trigger
    /// definition (P3, ADR-0002 strategy 2) — analyzed by
    /// <see cref="PgProgrammables"/>, never diffed structurally.
    /// </summary>
    internal IReadOnlyList<ProgrammableFile> ProgrammableFiles { get; }

    public IReadOnlyList<RawMessage> Warnings { get; }

    public IReadOnlyList<RawMessage> Errors { get; }

    /// <summary>Downcast with a real error, mirroring the SQL Server provider's guard.</summary>
    public static PgDesiredState From(IDesiredState state) => state as PgDesiredState
        ?? throw new ArgumentException(
            $"The desired state was not loaded by this provider (got {state.GetType().Name}).", nameof(state));

    public static PgDesiredState Load(string directory)
    {
        var modelTexts = new List<string>();
        var programmableFiles = new List<ProgrammableFile>();
        var warnings = new List<RawMessage>();
        var errors = new List<RawMessage>();

        foreach (var path in Directory.EnumerateFiles(directory, "*.sql", SearchOption.AllDirectories)
                     .OrderBy(p => p, StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(directory, path);
            var text = File.ReadAllText(path);

            var parsed = Parser.Parse(text);
            if (parsed.Error is not null || parsed.Value is null)
            {
                errors.Add(new RawMessage("Error", "SCHEMORPH007",
                    $"{relative}: does not parse as PostgreSQL " +
                    $"(position {parsed.Error?.CursorPos ?? 0}): {parsed.Error?.Message}"));
                continue;
            }
            if (parsed.Value.Stmts.Count == 0) continue;   // empty or comment-only

            var programmableCount = parsed.Value.Stmts.Count(s => ProgrammableKind(s.Stmt) is not null);
            if (programmableCount > 0)
            {
                // ADR-0002: one programmable object per file, same rule the SQL
                // Server provider enforces (there, discovered post-hoc across the
                // whole model; here, discoverable per-file because loading is
                // already per-file) — a file mixing a view with other statements,
                // or declaring two, would redefine more than the caller reviewed.
                if (programmableCount > 1 || programmableCount != parsed.Value.Stmts.Count)
                {
                    errors.Add(new RawMessage("Error", "SCHEMORPH004",
                        $"{relative}: one programmable object per file (ADR-0002) — found " +
                        $"{programmableCount} programmable statement(s) among " +
                        $"{parsed.Value.Stmts.Count} total in this file."));
                    continue;
                }

                programmableFiles.Add(new ProgrammableFile(relative, text));
                continue;
            }

            // CONCURRENTLY buys its lock-free build by refusing to run inside a
            // transaction, and an apply here is one transaction the tool owns —
            // that is the atomicity this provider declares. Honoring the file
            // would mean dropping either the keyword or the guarantee without
            // saying so, and both are the caller's to trade, not ours.
            if (parsed.Value.Stmts.Any(s => s.Stmt is { IndexStmt.Concurrent: true }))
            {
                throw Unsupported(
                    $"CONCURRENTLY index builds ({relative}) — an apply runs in one transaction, " +
                    "which a concurrent build cannot join");
            }

            if (parsed.Value.Stmts.All(s => IsModelStatement(s.Stmt)))
            {
                modelTexts.Add(text);
            }
            else
            {
                warnings.Add(new RawMessage("Warning", "SCHEMORPH006",
                    $"Skipped {relative}: contains statements that are not declarative DDL " +
                    "(DML, grants, or other imperative content is not desired state)."));
            }
        }

        return new PgDesiredState(modelTexts, programmableFiles, warnings, errors);
    }

    private static UnsupportedByProviderException Unsupported(string capability)
        => new(PostgresProvider.ProviderName, capability,
            string.Join(", ", PostgresProvider.DeclaredCapabilities));

    private static bool IsModelStatement(Node statement) => statement switch
    {
        { CreateStmt: not null } => true,
        { AlterTableStmt: not null } => true,
        { IndexStmt: not null } => true,
        { CreateSchemaStmt: not null } => true,
        _ => false,
    };

    private static string? ProgrammableKind(Node statement) => statement switch
    {
        { ViewStmt: not null } => "CREATE VIEW",
        { CreateFunctionStmt.IsProcedure: true } => "CREATE PROCEDURE",
        { CreateFunctionStmt: not null } => "CREATE FUNCTION",
        { CreateTrigStmt: not null } => "CREATE TRIGGER",
        _ => null,
    };
}
