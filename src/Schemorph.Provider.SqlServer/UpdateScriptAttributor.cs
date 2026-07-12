using System.Text;
using System.Text.RegularExpressions;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using Schemorph.Core.Providers;

namespace Schemorph.Provider.SqlServer;

/// <summary>
/// Splits the DacFx update script into per-object segments (plan explanations:
/// per-change SQL). DacFx announces every object's work with a PRINT batch —
/// <c>PRINT N'Altering Table [dbo].[X]...';</c> / <c>PRINT N'Starting rebuilding
/// table [dbo].[X]...';</c> — so attribution follows the generator's own
/// markers instead of re-inferring targets from statements. The generator's
/// format is stable under the exact-pin DacFx policy (upgrades are gated on the
/// golden corpus); if the markers ever change, attribution degrades to nothing
/// and every declarative <c>sql</c> stays null — never a wrong attachment.
/// </summary>
internal static partial class UpdateScriptAttributor
{
    // A whole batch that is a single PRINT — either a segment announcement or
    // progress chatter ('Update complete.'); never payload.
    [GeneratedRegex(@"^PRINT\s+N'(?<text>[^']*)';?\s*$")]
    private static partial Regex PrintBatch();

    // Announcement text: leading verb/type words, then the bracketed name
    // ([dbo].[X], or [dbo].[X].[IX_...] for indexes — the object is the first
    // two parts). 'rebuild' anywhere in the text marks a table rebuild.
    [GeneratedRegex(@"^[A-Za-z][A-Za-z ]*\s(?<name>\[[^\]]+\](\.\[[^\]]+\])+)\.\.\.$")]
    private static partial Regex Announcement();

    public static IReadOnlyList<ChangeScript> Attribute(string updateScript, IReadOnlyList<RawChange> changes)
    {
        var changeNames = changes.Select(c => c.ObjectName).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // objectName -> (sql segments, rebuild) — one object can own several
        // announced segments (e.g. ALTER TABLE + CREATE INDEX); they concatenate.
        var segments = new Dictionary<string, (StringBuilder Sql, bool Rebuild)>(StringComparer.OrdinalIgnoreCase);
        string? current = null;

        foreach (var batch in SqlBatchSplitter.Split(updateScript))
        {
            var trimmed = batch.Trim();
            if (PrintBatch().Match(trimmed) is { Success: true } print)
            {
                var text = print.Groups["text"].Value;
                if (Announcement().Match(text) is { Success: true } announced)
                {
                    var name = ObjectName(announced.Groups["name"].Value);
                    var rebuild = text.Contains("rebuild", StringComparison.OrdinalIgnoreCase);
                    // Only track objects the comparison actually reported; DacFx
                    // side-work (e.g. refreshing dependent views) is not a change.
                    current = changeNames.Contains(name) ? name : null;
                    if (current is not null)
                    {
                        (StringBuilder Sql, bool Rebuild) entry = segments.TryGetValue(current, out var existing)
                            ? existing
                            : (new StringBuilder(), false);
                        segments[current] = (entry.Sql, entry.Rebuild || rebuild);
                    }
                }
                continue;   // progress chatter is never payload
            }

            // A PRINT the regex could not read (e.g. escaped quotes in a name) is
            // still chatter — attaching it to the open segment would be a wrong
            // attribution, so close the segment instead (missing beats wrong).
            if (trimmed.StartsWith("PRINT ", StringComparison.OrdinalIgnoreCase))
            {
                current = null;
                continue;
            }

            if (current is null || IsScaffolding(trimmed))
            {
                continue;
            }

            var (sql, _) = segments[current];
            if (sql.Length > 0) sql.AppendLine("GO");
            sql.AppendLine(trimmed);
        }

        return segments
            .Select(kv =>
            {
                var sql = kv.Value.Sql.ToString().TrimEnd();
                return new ChangeScript(kv.Key, sql, kv.Value.Rebuild, AddsNotNullWithoutDefault(sql));
            })
            .Where(s => s.Sql.Length > 0)
            .ToList();
    }

    /// <summary>
    /// Safety-lint dialect judgment: the slice adds a NOT NULL column with no
    /// default, which fails on any table that already holds rows. Two shapes
    /// prove it — an in-place <c>ALTER TABLE ... ADD</c>, and a table rebuild
    /// whose row-copy <c>INSERT</c> omits the column newly declared NOT NULL in
    /// the rebuilt table. Proven from the AST only — identity/computed columns
    /// provide their own values, a CREATE with no row-copy has no rows to fail,
    /// and an unparseable slice proves nothing.
    /// </summary>
    private static bool AddsNotNullWithoutDefault(string sql)
    {
        var parser = new TSql150Parser(initialQuotedIdentifiers: true);
        using var reader = new StringReader(sql);
        var fragment = parser.Parse(reader, out var parseErrors);
        if (parseErrors is { Count: > 0 })
        {
            return false;
        }

        var visitor = new NotNullAddVisitor();
        fragment.Accept(visitor);
        return visitor.Found;
    }

    /// <summary>
    /// Detects a NOT NULL-without-default addition on either DacFx shape: the
    /// in-place ALTER ADD, or a rebuild where the new table declares the column
    /// NOT NULL but the row-copy INSERT does not carry it. For the rebuild shape
    /// it correlates each CREATE TABLE's hazardous columns with the column list
    /// of an INSERT targeting that same table — a CREATE with no such INSERT
    /// (a fresh table) copies no rows and cannot fail.
    /// </summary>
    private sealed class NotNullAddVisitor : TSqlFragmentVisitor
    {
        // rebuilt/new table name -> its NOT NULL-without-default columns.
        private readonly Dictionary<string, List<string>> _hazardousColumns = new(StringComparer.OrdinalIgnoreCase);
        // table name -> columns its row-copy INSERT provides a value for.
        private readonly Dictionary<string, HashSet<string>> _copiedColumns = new(StringComparer.OrdinalIgnoreCase);

        public bool Found =>
            // ALTER ADD proves it outright. The rebuild shape needs BOTH a
            // row-copy INSERT into the table (proving existing rows are carried
            // over — a fresh CREATE with none has no rows to fail) AND that copy
            // omitting a column the rebuilt table declares NOT NULL.
            _alterAddFound
            || _hazardousColumns.Any(t =>
                _copiedColumns.TryGetValue(t.Key, out var copied)
                && t.Value.Any(col => !copied.Contains(col)));

        private bool _alterAddFound;

        public override void Visit(AlterTableAddTableElementStatement node)
        {
            foreach (var column in node.Definition.ColumnDefinitions)
            {
                if (IsHazardous(column))
                {
                    _alterAddFound = true;
                }
            }
        }

        public override void Visit(CreateTableStatement node)
        {
            var table = Name(node.SchemaObjectName);
            foreach (var column in node.Definition.ColumnDefinitions)
            {
                if (IsHazardous(column))
                {
                    (_hazardousColumns.TryGetValue(table, out var list)
                        ? list
                        : _hazardousColumns[table] = new List<string>())
                        .Add(column.ColumnIdentifier.Value);
                }
            }
        }

        public override void Visit(InsertStatement node)
        {
            if (node.InsertSpecification.Target is not NamedTableReference target)
            {
                return;
            }

            // Only an explicit column list proves which columns get a value; a
            // bare INSERT ... SELECT cannot prove the hazardous column is omitted.
            var columns = node.InsertSpecification.Columns;
            if (columns.Count == 0)
            {
                return;
            }

            var table = Name(target.SchemaObject);
            var copied = _copiedColumns.TryGetValue(table, out var set)
                ? set
                : _copiedColumns[table] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var column in columns)
            {
                if (column.MultiPartIdentifier?.Identifiers is { Count: > 0 } parts)
                {
                    copied.Add(parts[^1].Value);
                }
            }
        }

        private static bool IsHazardous(ColumnDefinition column) =>
            column.Constraints.OfType<NullableConstraintDefinition>().Any(c => !c.Nullable)
            && column.DefaultConstraint is null
            && column.IdentityOptions is null
            && column.ComputedColumnExpression is null;

        private static string Name(SchemaObjectName name) =>
            string.Join('.', name.Identifiers.Select(i => i.Value));
    }

    /// <summary>The generator's transaction/error bookkeeping between segments.</summary>
    private static bool IsScaffolding(string batch) =>
        batch.StartsWith("IF @@ERROR", StringComparison.OrdinalIgnoreCase)
        || batch.StartsWith("IF @@TRANCOUNT", StringComparison.OrdinalIgnoreCase)
        || batch.Contains("#tmpErrors", StringComparison.OrdinalIgnoreCase);

    /// <summary>[dbo].[X] or [dbo].[X].[IX_Y] → "dbo.X" (an index belongs to its table's change).</summary>
    private static string ObjectName(string bracketed)
    {
        var parts = bracketed.Split("].[");
        var schema = parts[0].TrimStart('[');
        var name = parts[1].TrimEnd(']');
        return $"{schema}.{name}";
    }
}
