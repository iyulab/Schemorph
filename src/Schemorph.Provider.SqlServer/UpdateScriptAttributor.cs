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

    /// <summary>One announced run of batches, before it is attributed to a change.</summary>
    private sealed class Segment
    {
        public required string AnnouncedName { get; init; }
        public StringBuilder Sql { get; } = new();
        public bool Rebuild { get; set; }
    }

    public static IReadOnlyList<ChangeScript> Attribute(string updateScript, IReadOnlyList<RawChange> changes)
    {
        var changeNames = changes.Select(c => c.ObjectName).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Segments in script order; one change can own several (e.g. ALTER TABLE
        // then CREATE INDEX), and they concatenate in that order.
        var segments = new List<Segment>();
        Segment? current = null;

        foreach (var batch in SqlBatchSplitter.Split(updateScript))
        {
            var trimmed = batch.Trim();
            if (PrintBatch().Match(trimmed) is { Success: true } print)
            {
                var text = print.Groups["text"].Value;
                if (Announcement().Match(text) is { Success: true } announced)
                {
                    // Every announced run is captured, even when the announced
                    // name is not itself a reported change — DacFx announces a
                    // table's work under the *constraint* it is touching, so the
                    // owner is recovered below from the statements themselves.
                    current = new Segment { AnnouncedName = ObjectName(announced.Groups["name"].Value) };
                    current.Rebuild = text.Contains("rebuild", StringComparison.OrdinalIgnoreCase);
                    segments.Add(current);
                }
                else
                {
                    // Progress chatter ('Checking existing data against newly
                    // created constraints', 'Update complete.') marks the end of
                    // the announced work, not more of it. Leaving the segment open
                    // let the generator's post-commit batches flow into the last
                    // announced object.
                    current = null;
                }
                continue;   // a PRINT itself is never payload
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

            if (current.Sql.Length > 0) current.Sql.AppendLine("GO");
            current.Sql.AppendLine(trimmed);
        }

        // objectName -> (sql, rebuild), preserving first-seen order.
        var attributed = new Dictionary<string, (StringBuilder Sql, bool Rebuild)>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();

        foreach (var segment in segments)
        {
            var owner = Owner(segment, changeNames);
            if (owner is null)
            {
                continue;
            }

            if (!attributed.TryGetValue(owner, out var entry))
            {
                entry = (new StringBuilder(), false);
                order.Add(owner);
            }
            if (entry.Sql.Length > 0) entry.Sql.AppendLine("GO");
            entry.Sql.Append(segment.Sql);
            attributed[owner] = (entry.Sql, entry.Rebuild || segment.Rebuild);
        }

        return order
            .Select(name =>
            {
                var sql = attributed[name].Sql.ToString().TrimEnd();
                return new ChangeScript(name, sql, attributed[name].Rebuild, AddsNotNullWithoutDefault(sql));
            })
            .Where(s => s.Sql.Length > 0)
            .ToList();
    }

    /// <summary>
    /// Which reported change a segment belongs to, or null when it cannot be
    /// proven. Two sources of evidence, in order of directness:
    ///
    /// 1. The announced name is itself a reported change (the common case; an
    ///    index announced as <c>[dbo].[T].[IX]</c> already reduces to its table).
    /// 2. Otherwise the announced name is a dependent object DacFx names in its
    ///    own right — a check, default, or foreign-key constraint — while the
    ///    reported change is the table that owns it. The statements say which
    ///    table that is: every one of them is <c>ALTER TABLE &lt;owner&gt; …</c>.
    ///    Attribution follows the statements' own target, which is stronger
    ///    evidence than the marker, and only when they agree on a single table
    ///    that the comparison actually reported.
    ///
    /// Anything else — an unparseable segment, statements touching more than one
    /// table, a target that is not a reported change — resolves to null. A
    /// missing slice is honest; a wrong one is not.
    /// </summary>
    private static string? Owner(Segment segment, HashSet<string> changeNames)
    {
        if (changeNames.Contains(segment.AnnouncedName))
        {
            return segment.AnnouncedName;
        }

        var sql = segment.Sql.ToString();
        if (sql.Length == 0)
        {
            return null;
        }

        var parser = new TSql150Parser(initialQuotedIdentifiers: true);
        using var reader = new StringReader(sql);
        var fragment = parser.Parse(reader, out var parseErrors);
        if (parseErrors is { Count: > 0 } || fragment is not TSqlScript script)
        {
            return null;
        }

        string? owner = null;
        foreach (var statement in script.Batches.SelectMany(b => b.Statements))
        {
            // Only ALTER TABLE proves ownership. A segment mixing in anything else
            // is not a constraint's segment, and guessing from it would be exactly
            // the wrong attachment this attributor refuses to make.
            if (statement is not AlterTableStatement alter)
            {
                return null;
            }

            var target = string.Join('.', alter.SchemaObjectName.Identifiers.Select(i => i.Value));
            if (owner is not null && !owner.Equals(target, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
            owner = target;
        }

        return owner is not null && changeNames.Contains(owner) ? owner : null;
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

    /// <summary>The generator's transaction/error/session bookkeeping between segments.</summary>
    private static bool IsScaffolding(string batch) =>
        batch.StartsWith("IF @@ERROR", StringComparison.OrdinalIgnoreCase)
        || batch.StartsWith("IF @@TRANCOUNT", StringComparison.OrdinalIgnoreCase)
        || batch.StartsWith("USE ", StringComparison.OrdinalIgnoreCase)
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
