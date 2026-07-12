using System.Text.RegularExpressions;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using Schemorph.Core.Providers;

namespace Schemorph.Provider.SqlServer;

/// <summary>
/// The boundary between desired state and everything else living in a schema
/// directory. Real projects keep deploy scripts next to their model files —
/// SQLCMD :r includes, EXEC-only post-deploy touch-ups, seed DML; SSDT even has
/// dedicated item types (Build vs PreDeploy/PostDeploy) for exactly this split.
/// Schemorph's contract is "desired state = declarative DDL", enforced here by
/// *classification*, not by directory convention: any layout works (SSDT trees
/// included), and non-model files are skipped loudly instead of poisoning the
/// DacFx model with an unattributed SQL46010.
///
/// Failure asymmetry drives the design: a false skip silently turns a real
/// object into a pending DROP, so skipping happens only on positive evidence.
/// The parser runs FIRST — SQLCMD markers inside comments don't affect parsing,
/// so a commented ":r example" can never skip a legitimate model file. A file
/// that fails to parse *without* SQLCMD evidence is a hard, file-attributed
/// error, never a skip.
/// </summary>
internal static class DesiredStateLoader
{
    internal sealed record ModelFile(string Path, string Text);

    internal sealed record LoadResult(
        IReadOnlyList<ModelFile> ModelFiles,
        IReadOnlyList<RawMessage> Warnings,
        IReadOnlyList<RawMessage> Errors);

    // SQLCMD is a client-side dialect: colon directives and $(var) substitution
    // never reach the server as T-SQL. These only classify *why a parse failed* —
    // they are never checked against comment content.
    private static readonly Regex SqlCmdDirective = new(
        @"^\s*:(r|setvar|connect|on\s+error|out|error|!!)\b",
        RegexOptions.IgnoreCase | RegexOptions.Multiline, TimeSpan.FromSeconds(5));

    private static readonly Regex SqlCmdVariable = new(
        @"\$\(\w+\)", RegexOptions.None, TimeSpan.FromSeconds(5));

    internal static LoadResult Load(string desiredStateDirectory)
    {
        var modelFiles = new List<ModelFile>();
        var warnings = new List<RawMessage>();
        var errors = new List<RawMessage>();

        foreach (var file in Directory.GetFiles(desiredStateDirectory, "*.sql", SearchOption.AllDirectories)
                     .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            var text = File.ReadAllText(file);

            var parser = new TSql150Parser(initialQuotedIdentifiers: true);
            using var reader = new StringReader(text);
            var fragment = parser.Parse(reader, out var parseErrors);

            if (parseErrors is { Count: > 0 })
            {
                // Parse failure + SQLCMD evidence = a deploy script; without that
                // evidence it is a broken model file and must fail, not vanish.
                if (SqlCmdDirective.IsMatch(text) || SqlCmdVariable.IsMatch(text))
                {
                    warnings.Add(new RawMessage("Warning", "SCHEMORPH005",
                        $"Skipped {file}: SQLCMD syntax (:r / :setvar / $(var)) marks a deploy script, " +
                        "not desired state. Keep deploy scripts outside the --schema directory."));
                }
                else
                {
                    var first = parseErrors[0];
                    errors.Add(new RawMessage("Error", "SCHEMORPH007",
                        $"{file}({first.Line},{first.Column}): {first.Message}"));
                }
                continue;
            }

            // $(var) can survive parsing inside bracketed identifiers; catch it in
            // code tokens only (comments and string literals stay exempt).
            if (UsesSqlCmdVariableInCode(fragment))
            {
                warnings.Add(new RawMessage("Warning", "SCHEMORPH005",
                    $"Skipped {file}: SQLCMD variable substitution ($(var)) marks a deploy script, " +
                    "not desired state. Keep deploy scripts outside the --schema directory."));
                continue;
            }

            var imperative = FirstImperativeStatement(fragment);
            if (imperative is not null)
            {
                warnings.Add(new RawMessage("Warning", "SCHEMORPH006",
                    $"Skipped {file}: contains an imperative statement ({FriendlyName(imperative)}) — " +
                    "desired state files hold only declarative DDL. Move deploy/seed logic out of " +
                    "--schema, or split it into a versioned migration."));
                continue;
            }

            modelFiles.Add(new ModelFile(file, text));
        }

        return new LoadResult(modelFiles, warnings, errors);
    }

    private static bool UsesSqlCmdVariableInCode(TSqlFragment fragment) =>
        fragment.ScriptTokenStream is { } tokens && tokens.Any(t =>
            t.TokenType is not (TSqlTokenType.MultilineComment or TSqlTokenType.SingleLineComment
                or TSqlTokenType.AsciiStringLiteral or TSqlTokenType.UnicodeStringLiteral)
            && t.Text is { } tokenText && SqlCmdVariable.IsMatch(tokenText));

    private static TSqlStatement? FirstImperativeStatement(TSqlFragment fragment)
    {
        if (fragment is not TSqlScript script) return null;
        return script.Batches
            .SelectMany(b => b.Statements)
            .FirstOrDefault(IsImperative);
    }

    /// <summary>
    /// Statements that *do* something at runtime rather than *declare* an object.
    /// Deliberately a blacklist: an unknown statement kind flows through to DacFx
    /// (worst case: today's model error, now file-attributed), whereas a whitelist
    /// omission would silently skip a real object and surface as a phantom DROP.
    /// SET options (ANSI_NULLS / QUOTED_IDENTIFIER) are NOT listed — SSMS-scripted
    /// model files legitimately carry them. GRANT/DENY are NOT listed — permissions
    /// can be desired state.
    /// </summary>
    private static bool IsImperative(TSqlStatement statement) => statement
        is SelectStatement
        or InsertStatement
        or UpdateStatement
        or DeleteStatement
        or MergeStatement
        or TruncateTableStatement
        or ExecuteStatement
        or PrintStatement
        or IfStatement
        or WhileStatement
        or DeclareVariableStatement
        or SetVariableStatement
        or UseStatement
        or BeginTransactionStatement
        or CommitTransactionStatement
        or RollbackTransactionStatement
        or WaitForStatement
        or RaiseErrorStatement
        or ThrowStatement
        or KillStatement
        or CheckpointStatement
        or DbccStatement
        or BackupStatement
        or RestoreStatement;

    private static string FriendlyName(TSqlStatement statement)
    {
        var name = statement.GetType().Name;
        return (name.EndsWith("Statement", StringComparison.Ordinal) ? name[..^"Statement".Length] : name)
            .ToUpperInvariant();
    }
}
