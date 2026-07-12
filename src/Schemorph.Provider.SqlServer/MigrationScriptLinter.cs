using Microsoft.SqlServer.TransactSql.ScriptDom;
using Schemorph.Core.Providers;

namespace Schemorph.Provider.SqlServer;

/// <summary>
/// Dialect half of the migration safety lint: which risky constructs a script
/// provably contains (AST judgment, never regex-over-text). An unparseable
/// script yields no signals — the conservative contract on
/// <see cref="IDatabaseProvider.LintMigrationScriptAsync"/>.
/// </summary>
internal static class MigrationScriptLinter
{
    public static IReadOnlyList<MigrationLintSignal> Lint(string scriptText)
    {
        var parser = new TSql150Parser(initialQuotedIdentifiers: true);
        using var reader = new StringReader(scriptText);
        var fragment = parser.Parse(reader, out var parseErrors);
        if (parseErrors is { Count: > 0 })
        {
            return Array.Empty<MigrationLintSignal>();
        }

        var visitor = new SignalVisitor();
        fragment.Accept(visitor);
        return visitor.Signals.Order().ToList();
    }

    private sealed class SignalVisitor : TSqlFragmentVisitor
    {
        public HashSet<MigrationLintSignal> Signals { get; } = new();

        public override void Visit(TruncateTableStatement node) => Signals.Add(MigrationLintSignal.Truncate);

        public override void Visit(UpdateStatement node)
        {
            if (node.UpdateSpecification.WhereClause is null)
            {
                Signals.Add(MigrationLintSignal.UnfilteredUpdate);
            }
        }

        public override void Visit(DeleteStatement node)
        {
            if (node.DeleteSpecification.WhereClause is null)
            {
                Signals.Add(MigrationLintSignal.UnfilteredDelete);
            }
        }

        public override void Visit(GrantStatement node) => Signals.Add(MigrationLintSignal.PermissionChange);

        public override void Visit(RevokeStatement node) => Signals.Add(MigrationLintSignal.PermissionChange);

        public override void Visit(DenyStatement node) => Signals.Add(MigrationLintSignal.PermissionChange);
    }
}
