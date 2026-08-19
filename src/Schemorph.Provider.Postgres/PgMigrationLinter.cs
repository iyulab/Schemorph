using Google.Protobuf;
using Google.Protobuf.Reflection;
using PgSqlParser;
using Schemorph.Core.Providers;

namespace Schemorph.Provider.Postgres;

/// <summary>
/// Dialect half of the migration safety lint (P4): which risky constructs a
/// script provably contains — AST judgment, never regex-over-text, the same
/// discipline <see cref="Shadow.SchemaRewriter"/> and <see cref="PgProgrammables"/>
/// already hold. An unparseable script yields no signals, the conservative
/// contract on <c>IDatabaseProvider.LintMigrationScriptAsync</c>.
///
/// Walked generically over the whole protobuf tree rather than only the
/// top-level statement list, so a signal inside any nested construct this
/// grammar actually parses is found the same way <see cref="PgProgrammables"/>
/// finds a <c>RangeVar</c> anywhere in a view's query — coverage does not
/// depend on enumerating statement positions.
/// </summary>
internal static class PgMigrationLinter
{
    public static IReadOnlyList<MigrationLintSignal> Lint(string scriptText)
    {
        var parsed = Parser.Parse(scriptText);
        if (parsed.Error is not null || parsed.Value is null)
        {
            return Array.Empty<MigrationLintSignal>();
        }

        var signals = new SortedSet<MigrationLintSignal>();
        Walk(parsed.Value, signals);
        return signals.ToList();
    }

    private static void Walk(IMessage message, ISet<MigrationLintSignal> signals)
    {
        if (message is Node node)
        {
            if (node.TruncateStmt is not null) signals.Add(MigrationLintSignal.Truncate);
            if (node.UpdateStmt is { WhereClause: null }) signals.Add(MigrationLintSignal.UnfilteredUpdate);
            if (node.DeleteStmt is { WhereClause: null }) signals.Add(MigrationLintSignal.UnfilteredDelete);
            // One node covers GRANT and REVOKE alike (IsGrant); PostgreSQL has no
            // DENY (a T-SQL-only construct SQL Server's linter also checks).
            if (node.GrantStmt is not null) signals.Add(MigrationLintSignal.PermissionChange);
        }

        foreach (var field in message.Descriptor.Fields.InDeclarationOrder())
        {
            if (field.FieldType != FieldType.Message) continue;
            var value = field.Accessor.GetValue(message);

            if (field.IsRepeated)
            {
                foreach (var item in ((System.Collections.IEnumerable)value).OfType<IMessage>())
                {
                    Walk(item, signals);
                }
            }
            else if (value is IMessage child)
            {
                Walk(child, signals);
            }
        }
    }
}
