using Google.Protobuf;
using Google.Protobuf.Reflection;
using PgSqlParser;

namespace Schemorph.Provider.Postgres.Shadow;

/// <summary>
/// Retargets desired-state DDL into the shadow schema by rewriting the parse
/// tree, never the text (ADR-0007 addendum). String substitution was refuted
/// by measurement — <c>pg_get_indexdef</c> renders fold-safe qualifiers
/// unquoted, so a textual rewrite misses them and the statement lands in the
/// source schema (cycle-76).
///
/// The walk is generic over the protobuf AST so statement coverage does not
/// depend on us enumerating statement types: every <c>schemaname</c> field is
/// a schema by construction, and the identifier lists that carry a qualifier
/// as their first element (<c>TypeName.names</c>, <c>FuncCall.funcname</c>,
/// three-part <c>ColumnRef.fields</c>, <c>ObjectWithArgs.objname</c>) are
/// rewritten by node type — a bare <c>String</c> node whose value happens to
/// equal the schema name (a column called like the schema) is never touched.
///
/// Deparse output is executed against the scratch schema only and is never
/// compared as text — both comparison sides are read back from
/// <c>pg_catalog</c> — so deparse formatting costs nothing here. Verbatim
/// remains the rule where text is the artifact.
/// </summary>
internal static class SchemaRewriter
{
    /// <summary>
    /// Parse <paramref name="sql"/>, retarget every reference to
    /// <paramref name="sourceSchema"/> onto <paramref name="shadowSchema"/>,
    /// and render the result. References to other schemas pass through
    /// untouched (cross-schema scope is a later slice, per ADR-0007).
    /// </summary>
    /// <exception cref="SchemaRewriteException">The text is not valid PostgreSQL.</exception>
    public static string Retarget(string sql, string sourceSchema, string shadowSchema)
        => RetargetSet([sql], sourceSchema, shadowSchema);

    /// <summary>
    /// Retarget a whole desired-state set and put its statements into a
    /// dependency-safe order: tables first, then non-FK constraints, then
    /// foreign keys, then indexes. Desired-state files carry no reliable
    /// order of their own — the first live corpus applied its files
    /// alphabetically and the FK arrived before the table it references.
    /// The classes mirror pg_dump's pre-data/post-data split; ordering
    /// *within* a class is preserved. A cross-table FK declared inline in a
    /// later CREATE TABLE is out of this slice's reach (statement-level
    /// reordering cannot split a statement) and surfaces as the engine's own
    /// error rather than being silently reshuffled.
    /// </summary>
    public static string RetargetSet(
        IReadOnlyList<string> sqlTexts, string sourceSchema, string shadowSchema)
    {
        var combined = new ParseResult();
        foreach (var sql in sqlTexts)
        {
            var parsed = Parser.Parse(sql);
            if (parsed.Error is not null || parsed.Value is null)
            {
                throw new SchemaRewriteException(
                    parsed.Error?.Message ?? "The parser returned no tree.",
                    parsed.Error?.CursorPos ?? 0);
            }
            Walk(parsed.Value, sourceSchema, shadowSchema);
            combined.Version = parsed.Value.Version;
            combined.Stmts.AddRange(parsed.Value.Stmts);
        }

        foreach (var statement in combined.Stmts)
        {
            if (statement.Stmt?.CreateSchemaStmt is not { } createSchema) continue;

            // The target schema's own CREATE SCHEMA is legitimate model content
            // ("schemas" capability line); the scratch schema already exists, so
            // it becomes idempotent. Any OTHER schema would be created for real
            // by a shadow apply — outside the sandbox — so it is refused.
            if (createSchema.Schemaname == shadowSchema)
            {
                createSchema.IfNotExists = true;
            }
            else
            {
                throw new SchemaRewriteException(
                    $"CREATE SCHEMA {createSchema.Schemaname}: only the target schema's own " +
                    "CREATE SCHEMA belongs to this slice (cross-schema DDL is a later slice).", 0);
            }
        }

        var ordered = combined.Stmts.OrderBy(OrderClass).ToList();   // OrderBy is stable
        ordered = OrderCreatesByInlineForeignKeys(ordered);
        combined.Stmts.Clear();
        combined.Stmts.AddRange(ordered);

        var deparsed = Parser.Deparse(combined);
        if (deparsed.Error is not null || deparsed.Value is null)
        {
            throw new SchemaRewriteException(
                $"Deparse failed after rewriting: {deparsed.Error?.Message}", 0);
        }
        return deparsed.Value;
    }

    private static int OrderClass(RawStmt statement) => statement.Stmt switch
    {
        { CreateStmt: not null } => 0,
        { AlterTableStmt: { } alter } when alter.Cmds.Any(IsForeignKeyAdd) => 2,
        { IndexStmt: not null } => 3,
        _ => 1,
    };

    private static bool IsForeignKeyAdd(Node command)
        => command.AlterTableCmd?.Def?.Constraint?.Contype == ConstrType.ConstrForeign;

    /// <summary>
    /// Statement-class ordering is not enough for the most common file shape:
    /// a CREATE TABLE with its FOREIGN KEY declared INLINE references a table
    /// that must already exist. The creates are therefore topologically sorted
    /// by those inline references (self-references are fine; a reference to a
    /// table this set does not create is left to the engine). Mutual foreign
    /// keys cannot be solved by ordering whole statements — that shape needs
    /// ALTER-separated constraints, and the error says so.
    /// </summary>
    private static List<RawStmt> OrderCreatesByInlineForeignKeys(List<RawStmt> statements)
    {
        var creates = statements.Where(s => s.Stmt?.CreateStmt is not null).ToList();
        if (creates.Count <= 1) return statements;

        var byName = creates.ToDictionary(s => s.Stmt.CreateStmt.Relation.Relname, StringComparer.Ordinal);
        var sorted = new List<RawStmt>();
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var done = new HashSet<string>(StringComparer.Ordinal);

        void Visit(RawStmt statement)
        {
            var name = statement.Stmt.CreateStmt.Relation.Relname;
            if (done.Contains(name)) return;
            if (!visiting.Add(name))
            {
                throw new SchemaRewriteException(
                    $"Tables reference each other in a cycle around {name}; mutual foreign keys " +
                    "must be declared as separate ALTER TABLE ... ADD CONSTRAINT statements.", 0);
            }
            foreach (var referenced in InlineForeignKeyTargets(statement))
            {
                if (referenced != name && byName.TryGetValue(referenced, out var dependency))
                {
                    Visit(dependency);
                }
            }
            visiting.Remove(name);
            done.Add(name);
            sorted.Add(statement);
        }

        foreach (var create in creates) Visit(create);

        // Splice the sorted creates back over the original create positions;
        // everything else keeps its place.
        var queue = new Queue<RawStmt>(sorted);
        return statements.Select(s => s.Stmt?.CreateStmt is null ? s : queue.Dequeue()).ToList();
    }

    private static IEnumerable<string> InlineForeignKeyTargets(RawStmt statement)
        => CollectForeignKeyTables(statement).Distinct(StringComparer.Ordinal);

    private static IEnumerable<string> CollectForeignKeyTables(IMessage message)
    {
        if (message.Descriptor.Name == "Constraint")
        {
            var constraint = (Constraint)message;
            if (constraint.Contype == ConstrType.ConstrForeign && constraint.Pktable is not null)
            {
                yield return constraint.Pktable.Relname;
            }
        }

        foreach (var field in message.Descriptor.Fields.InDeclarationOrder())
        {
            if (field.FieldType != FieldType.Message) continue;
            var value = field.Accessor.GetValue(message);
            if (field.IsRepeated)
            {
                foreach (var item in ((System.Collections.IEnumerable)value).OfType<IMessage>())
                {
                    foreach (var name in CollectForeignKeyTables(item)) yield return name;
                }
            }
            else if (value is IMessage child)
            {
                foreach (var name in CollectForeignKeyTables(child)) yield return name;
            }
        }
    }

    // Node types whose first list element is a schema qualifier when the list
    // has more than one part. Keyed by (message type, field name) so a String
    // that merely *looks* like the schema is never rewritten.
    private static readonly (string Message, string Field)[] QualifiedNameLists =
    {
        ("TypeName", "names"),
        ("FuncCall", "funcname"),
        ("ObjectWithArgs", "objname"),
        ("ColumnRef", "fields"),
    };

    private static void Walk(IMessage message, string source, string shadow)
    {
        var typeName = message.Descriptor.Name;

        foreach (var field in message.Descriptor.Fields.InDeclarationOrder())
        {
            var value = field.Accessor.GetValue(message);

            if (field.FieldType == FieldType.String)
            {
                // Every field the grammar calls "schemaname" is a schema
                // reference by construction (RangeVar, and with it every
                // table/index/ALTER target and FK pktable). An UNQUALIFIED
                // RangeVar in single-schema model DDL is target-relative —
                // left alone it would land wherever the connection's
                // search_path points, which is precisely not the sandbox.
                if (field.Name == "schemaname"
                    && ((string)value == source || (typeName == "RangeVar" && (string)value == "")))
                {
                    field.Accessor.SetValue(message, shadow);
                }
                continue;
            }

            if (field.FieldType != FieldType.Message) continue;

            if (field.IsRepeated)
            {
                var items = ((System.Collections.IEnumerable)value).OfType<IMessage>().ToList();

                if (QualifiedNameLists.Contains((typeName, field.Name)) && IsQualified(items, field.Name, typeName))
                {
                    RewriteFirstString(items[0], source, shadow);
                }

                foreach (var item in items) Walk(item, source, shadow);
            }
            else if (value is IMessage child)
            {
                Walk(child, source, shadow);
            }
        }
    }

    /// <summary>
    /// Does this identifier list start with a schema qualifier? Two-part type
    /// and function names are schema-qualified; a ColumnRef needs three parts
    /// (<c>schema.table.column</c>) before its head is a schema.
    /// </summary>
    private static bool IsQualified(IReadOnlyList<IMessage> items, string fieldName, string typeName)
        => typeName == "ColumnRef" ? items.Count >= 3 : items.Count >= 2;

    private static void RewriteFirstString(IMessage node, string source, string shadow)
    {
        // Identifier list elements arrive as Node { String { sval } } wrappers;
        // descend to the String message and rewrite its sval.
        var current = node;
        while (true)
        {
            if (current.Descriptor.Name == "String")
            {
                var sval = current.Descriptor.Fields.InDeclarationOrder()
                    .FirstOrDefault(f => f.Name == "sval");
                if (sval is not null && (string)sval.Accessor.GetValue(current) == source)
                {
                    sval.Accessor.SetValue(current, shadow);
                }
                return;
            }

            var inner = current.Descriptor.Fields.InDeclarationOrder()
                .Where(f => f.FieldType == FieldType.Message && !f.IsRepeated)
                .Select(f => f.Accessor.GetValue(current))
                .OfType<IMessage>()
                .FirstOrDefault();
            if (inner is null) return;
            current = inner;
        }
    }
}

/// <summary>The desired-state text could not be parsed or re-rendered.</summary>
internal sealed class SchemaRewriteException(string message, int cursorPosition)
    : Exception(message)
{
    /// <summary>1-based character position from the parser, 0 when unknown.</summary>
    public int CursorPosition { get; } = cursorPosition;
}
