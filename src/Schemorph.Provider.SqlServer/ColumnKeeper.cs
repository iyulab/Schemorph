using Microsoft.SqlServer.Dac;
using Microsoft.SqlServer.Dac.Compare;
using Microsoft.SqlServer.Dac.Model;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using Schemorph.Core.Providers;
using Index = Microsoft.SqlServer.Dac.Model.Index;

namespace Schemorph.Provider.SqlServer;

/// <summary>
/// Builds the desired model a comparison needs when a dropped column is to be kept: the desired state
/// as written, plus the column as the live table declares it, plus what hangs on that column in the
/// live database and the desired state does not declare (a default, a check, an index).
/// </summary>
/// <remarks>
/// <para>
/// DacFx publishes a difference whole. A table whose change is a column drop cannot be published without
/// the drop, and cannot be left out of the publish while another change depends on the table (a foreign
/// key added to a new table references it) — so a plan that withholds the drop has to withhold the
/// dependents too. Comparing against a desired state that still declares the column answers the question
/// differently: the engine never writes the drop, and what depends on the table runs.
/// </para>
/// <para>
/// What hangs on the column stays with it for the same reason the column does. Without its default, a
/// NOT NULL column would start failing every insert from code that no longer knows about it — a change
/// the plan never mentioned. Anything that cannot be carried over faithfully (a definition the engine
/// will not read back, an object whose script is unavailable, a model that no longer validates) is a
/// reason not to keep the column at all: the caller then withholds the table's change whole, as before.
/// </para>
/// </remarks>
internal static class ColumnKeeper
{
    internal sealed record Kept(IReadOnlyList<string> Batches, IReadOnlyList<WithheldColumnDrop> Withheld);

    /// <summary>What may hang on a column and is a separate element in the DacFx model.</summary>
    private static ModelTypeClass[] HangingTypes() =>
    [
        DefaultConstraint.TypeClass, CheckConstraint.TypeClass, Index.TypeClass,
        UniqueConstraint.TypeClass, ForeignKeyConstraint.TypeClass, PrimaryKeyConstraint.TypeClass,
    ];

    /// <summary>
    /// The desired batches with every dropped column kept, or null and the reason it could not be.
    /// </summary>
    /// <param name="desiredBatches">The desired state's model batches, as the comparison reads them.</param>
    /// <param name="first">The comparison against the desired state as written; it names the drops.</param>
    internal static (Kept? Kept, string? Why) TryKeep(
        IReadOnlyList<string> desiredBatches, string connectionString, SchemaComparisonResult first,
        CancellationToken cancellationToken)
    {
        try
        {
            return Keep(desiredBatches, connectionString, first, cancellationToken);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Anything this reads back from the engine can be something it will not read back; the table is
            // then withheld whole, which is the answer when nothing better can be proven.
            return (null, e.Message);
        }
    }

    private static (Kept? Kept, string? Why) Keep(
        IReadOnlyList<string> desiredBatches, string connectionString, SchemaComparisonResult first,
        CancellationToken cancellationToken)
    {
        var drops = SqlServerProvider.ColumnsDropped(first.Differences);
        if (drops.Count == 0) return (null, "no dropped column to keep");

        var deleted = DeletedObjects(first.Differences);
        var databaseName = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(connectionString).InitialCatalog;
        var dacpacPath = TemporaryWorkspace.NewFile("keep", ".dacpac");
        try
        {
            new DacServices(connectionString).Extract(
                dacpacPath, databaseName, "Schemorph", new Version(0, 0, 1), cancellationToken: cancellationToken);
            using var live = new TSqlModel(dacpacPath, DacSchemaModelStorageType.Memory);

            var definitions = new Dictionary<string, List<ColumnDefinition>>(StringComparer.OrdinalIgnoreCase);
            foreach (var (table, dropped) in drops)
            {
                var found = ColumnDefinitionsOf(live, table, dropped.Columns);
                if (found is null) return (null, $"the live definition of {table} could not be read back");
                definitions[table] = found;
            }

            var hanging = new List<string>();
            foreach (var obj in live.GetObjects(DacQueryScopes.UserDefined, HangingTypes()))
            {
                if (!deleted.Contains(SqlServerProvider.FullName(obj)) || !References(obj, drops)) continue;
                if (!obj.TryGetScript(out var script))
                    return (null, $"the definition of {SqlServerProvider.FullName(obj)} could not be read back");
                hanging.Add(script);
            }

            var batches = new List<string>(desiredBatches.Count + hanging.Count);
            var restored = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var batch in desiredBatches)
            {
                batches.Add(WithColumns(batch, definitions, restored) ?? batch);
            }
            var missing = drops.Keys.Where(t => !restored.Contains(t)).ToList();
            if (missing.Count > 0)
                return (null, $"the desired state's definition of {string.Join(", ", missing)} is not a CREATE TABLE this can add to");
            batches.AddRange(hanging);

            var withheld = drops
                .OrderBy(d => d.Key, StringComparer.Ordinal)
                .Select(d => new WithheldColumnDrop(
                    d.Key, d.Value.Columns, DropStatement(d.Value.Schema, d.Value.Table, d.Value.Columns)))
                .ToList();
            return (new Kept(batches, withheld), null);
        }
        finally
        {
            TemporaryWorkspace.TryDelete(dacpacPath);
        }
    }

    private static string DropStatement(string schema, string table, IReadOnlyList<string> columns) =>
        $"ALTER TABLE {Quote(schema)}.{Quote(table)} DROP COLUMN {string.Join(", ", columns.Select(Quote))};";

    private static string Quote(string identifier) => $"[{identifier.Replace("]", "]]", StringComparison.Ordinal)}]";

    /// <summary>The names of the objects the comparison would remove, anywhere in its tree.</summary>
    private static HashSet<string> DeletedObjects(IEnumerable<SchemaDifference> differences)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stack = new Stack<SchemaDifference>(differences);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            foreach (var child in node.Children) stack.Push(child);
            if (node.UpdateAction == SchemaUpdateAction.Delete && node.TargetObject is { } target)
                names.Add(SqlServerProvider.FullName(target));
        }
        return names;
    }

    /// <summary>Whether the object is defined in terms of one of the columns being kept.</summary>
    private static bool References(TSqlObject obj, IReadOnlyDictionary<string, SqlServerProvider.DroppedColumns> drops) =>
        obj.GetReferenced(DacQueryScopes.UserDefined).Any(r =>
            r.ObjectType == Column.TypeClass
            && r.Name.Parts.Count > 2
            && drops.TryGetValue(string.Join(".", r.Name.Parts.Take(2)), out var dropped)
            && dropped.Columns.Contains(r.Name.Parts[^1], StringComparer.OrdinalIgnoreCase));

    /// <summary>The live table's own definitions of the given columns, or null when they cannot all be read.</summary>
    private static List<ColumnDefinition>? ColumnDefinitionsOf(
        TSqlModel live, string table, IReadOnlyList<string> columns)
    {
        var obj = live.GetObjects(DacQueryScopes.UserDefined, Table.TypeClass)
            .FirstOrDefault(t => string.Equals(SqlServerProvider.FullName(t), table, StringComparison.OrdinalIgnoreCase));
        return obj is null || !obj.TryGetScript(out var script) ? null : DefinitionsIn(script, columns);
    }

    /// <summary>The definitions of the named columns in a <c>CREATE TABLE</c> script, or null when it has none of them.</summary>
    internal static List<ColumnDefinition>? DefinitionsIn(string createTableScript, IReadOnlyList<string> columns)
    {
        var create = Parse(createTableScript)?.Batches.SelectMany(b => b.Statements).OfType<CreateTableStatement>().FirstOrDefault();
        if (create is null) return null;

        var found = new List<ColumnDefinition>();
        foreach (var column in columns)
        {
            var definition = create.Definition.ColumnDefinitions.FirstOrDefault(
                c => string.Equals(c.ColumnIdentifier.Value, column, StringComparison.OrdinalIgnoreCase));
            if (definition is null) return null;
            found.Add(definition);
        }
        return found;
    }

    /// <summary>
    /// The batch with the kept columns added to the table it creates, or null when it creates none of
    /// the tables concerned. <paramref name="restored"/> collects the tables that were found.
    /// </summary>
    internal static string? WithColumns(
        string batch, Dictionary<string, List<ColumnDefinition>> definitions, HashSet<string> restored)
    {
        var script = Parse(batch);
        if (script is null) return null;

        var changed = false;
        foreach (var create in script.Batches.SelectMany(b => b.Statements).OfType<CreateTableStatement>())
        {
            var name = $"{create.SchemaObjectName.SchemaIdentifier?.Value ?? "dbo"}.{create.SchemaObjectName.BaseIdentifier.Value}";
            if (!definitions.TryGetValue(name, out var add)) continue;
            foreach (var column in add) create.Definition.ColumnDefinitions.Add(column);
            restored.Add(name);
            changed = true;
        }
        if (!changed) return null;

        var generator = new Sql150ScriptGenerator(new SqlScriptGeneratorOptions
        {
            KeywordCasing = KeywordCasing.Uppercase,
            IncludeSemicolons = true,
        });
        generator.GenerateScript(script, out var text);
        return text;
    }

    private static TSqlScript? Parse(string sql)
    {
        var parser = new TSql150Parser(initialQuotedIdentifiers: true);
        using var reader = new StringReader(sql);
        var fragment = parser.Parse(reader, out var errors);
        return errors.Count == 0 ? fragment as TSqlScript : null;
    }
}
