using PgSqlParser;
using Schemorph.Core.Providers;

namespace Schemorph.Provider.Postgres;

/// <summary>
/// A desired-state directory, loaded and classified once (the provider
/// boundary's single-load contract). Classification is parse-based, with the
/// real PostgreSQL grammar: a file either belongs to the declared slice
/// (tables, columns, constraints, schemas — plus the indexes inspect already
/// renders), is imperative content that is not desired state (skipped loudly,
/// the SQL Server convention), or demands a slice this provider has not
/// earned yet — and that last case REFUSES rather than skips, because a plan
/// that silently ignored a view file would claim a sync it cannot see.
/// </summary>
internal sealed class PgDesiredState : IDesiredState
{
    private PgDesiredState(
        IReadOnlyList<string> modelTexts,
        IReadOnlyList<RawMessage> warnings,
        IReadOnlyList<RawMessage> errors)
    {
        ModelTexts = modelTexts;
        Warnings = warnings;
        Errors = errors;
    }

    /// <summary>The model files' texts, in stable (path-ordered) order.</summary>
    public IReadOnlyList<string> ModelTexts { get; }

    public IReadOnlyList<RawMessage> Warnings { get; }

    public IReadOnlyList<RawMessage> Errors { get; }

    /// <summary>Downcast with a real error, mirroring the SQL Server provider's guard.</summary>
    public static PgDesiredState From(IDesiredState state) => state as PgDesiredState
        ?? throw new ArgumentException(
            $"The desired state was not loaded by this provider (got {state.GetType().Name}).", nameof(state));

    public static PgDesiredState Load(string directory)
    {
        var modelTexts = new List<string>();
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

            var programmable = parsed.Value.Stmts
                .Select(s => ProgrammableKind(s.Stmt)).FirstOrDefault(k => k is not null);
            if (programmable is not null)
            {
                // Not the user's error and not ignorable — the honest outcome is
                // the provider's own refusal, naming the slice that is missing.
                throw new UnsupportedByProviderException(
                    PostgresProvider.ProviderName,
                    $"programmable objects ({relative}: {programmable} — slice P3)",
                    string.Join(", ", PostgresProvider.DeclaredCapabilities));
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

        return new PgDesiredState(modelTexts, warnings, errors);
    }

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
