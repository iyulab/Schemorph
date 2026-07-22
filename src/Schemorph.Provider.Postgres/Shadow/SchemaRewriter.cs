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
    {
        var parsed = Parser.Parse(sql);
        if (parsed.Error is not null || parsed.Value is null)
        {
            throw new SchemaRewriteException(
                parsed.Error?.Message ?? "The parser returned no tree.",
                parsed.Error?.CursorPos ?? 0);
        }

        Walk(parsed.Value, sourceSchema, shadowSchema);

        var deparsed = Parser.Deparse(parsed.Value);
        if (deparsed.Error is not null || deparsed.Value is null)
        {
            throw new SchemaRewriteException(
                $"Deparse failed after rewriting: {deparsed.Error?.Message}", 0);
        }
        return deparsed.Value;
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
                // table/index/ALTER target and FK pktable).
                if (field.Name == "schemaname" && (string)value == source)
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
