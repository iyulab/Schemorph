using Google.Protobuf;
using Google.Protobuf.Reflection;
using PgSqlParser;
using Schemorph.Core.Providers;

namespace Schemorph.Provider.Postgres;

/// <summary>
/// Idempotent re-definition analysis for programmable objects (ADR-0002
/// strategy 2, P3): views, functions, procedures and triggers. PostgreSQL
/// gives all four kinds a native <c>CREATE OR REPLACE</c> / <c>CREATE OR
/// REPLACE TRIGGER</c> form, so — unlike the SQL Server provider, which
/// rewrites text with a regex because T-SQL's idempotent form is the
/// differently-spelled <c>CREATE OR ALTER</c> — the redefinition script here
/// is built by setting one AST flag and deparsing (the same
/// parse-tree-not-text discipline <see cref="Shadow.SchemaRewriter"/> already
/// uses), never by pattern-matching the keyword.
///
/// Single-schema scope (ADR-0007): object identity is the bare name, the same
/// convention <see cref="PgTable.Name"/> already uses for tables — this slice
/// does not qualify by schema, and a reference schema-qualified to something
/// else is read by its bare name too (cross-schema DDL is explicitly a later
/// slice, per <see cref="Shadow.SchemaRewriter"/>'s own boundary).
/// </summary>
internal static class PgProgrammables
{
    public static ProgrammableAnalysis Analyze(IReadOnlyList<PgDesiredState.ProgrammableFile> files)
    {
        var messages = new List<RawMessage>();
        var parsedObjects = new List<(string ObjectName, string ObjectType, string Path, string Text,
            string ApplyScript, IReadOnlyList<string> Referenced)>();

        foreach (var file in files)
        {
            // Load already parsed this file to classify it; re-parsing here
            // keeps PgDesiredState a pure classifier and this the only place
            // that owns the AST rewrite, at the cost of one cheap re-parse of
            // a single-statement file.
            var parsed = Parser.Parse(file.Text);
            if (parsed.Error is not null || parsed.Value is null || parsed.Value.Stmts.Count != 1)
            {
                messages.Add(new RawMessage("Error", "SCHEMORPH003",
                    $"{file.Path}: failed to re-parse a file already classified as a single " +
                    "programmable statement — this is an internal inconsistency, please report it."));
                continue;
            }

            var stmt = parsed.Value.Stmts[0].Stmt;
            var described = Describe(stmt);
            if (described is null)
            {
                messages.Add(new RawMessage("Error", "SCHEMORPH003",
                    $"{file.Path}: not a recognized programmable statement."));
                continue;
            }

            var (name, objectType, setReplace, selfReference) = described.Value;
            setReplace();

            var deparsed = Parser.Deparse(parsed.Value);
            if (deparsed.Error is not null || deparsed.Value is null)
            {
                messages.Add(new RawMessage("Error", "SCHEMORPH003",
                    $"{file.Path}: failed to render an idempotent redefinition " +
                    $"({deparsed.Error?.Message})."));
                continue;
            }

            var referenced = CollectRangeVarNames(stmt)
                .Where(n => n != selfReference)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            parsedObjects.Add((name, objectType, file.Path, file.Text, deparsed.Value, referenced));
        }

        foreach (var duplicate in parsedObjects.GroupBy(o => o.ObjectName, StringComparer.Ordinal)
                     .Where(g => g.Count() > 1))
        {
            messages.Add(new RawMessage("Error", "SCHEMORPH004",
                $"{duplicate.Key} is defined in more than one file: " +
                string.Join(", ", duplicate.Select(o => o.Path).OrderBy(p => p, StringComparer.Ordinal)) + "."));
        }
        if (messages.Any(m => m.Severity == "Error"))
        {
            return new ProgrammableAnalysis(Array.Empty<ProgrammableObjectInfo>(), messages);
        }

        var names = parsedObjects.Select(o => o.ObjectName).ToHashSet(StringComparer.Ordinal);
        var objects = parsedObjects.Select(o => new ProgrammableObjectInfo(
            o.ObjectName, o.ObjectType, o.Path, o.Text, o.ApplyScript,
            DependsOn: o.Referenced.Where(names.Contains)
                .OrderBy(n => n, StringComparer.Ordinal).ToList(),
            DependsOnTables: o.Referenced.Where(n => !names.Contains(n))
                .OrderBy(n => n, StringComparer.Ordinal).ToList()))
            .ToList();

        return new ProgrammableAnalysis(objects, messages);
    }

    /// <summary>
    /// This statement's bare object name, Schemorph object-type label (from
    /// <see cref="Schemorph.Core.Planning.ProgrammableObjects.ObjectTypes"/> —
    /// the vocabulary the redefine runner and plan builder already key on), the
    /// AST mutation that turns it idempotent, and the name to exclude from its
    /// own referenced-names walk (a view's <c>ViewStmt.View</c> would otherwise
    /// count as a reference to itself; a trigger's own table is a real
    /// dependency and is deliberately not excluded here).
    /// </summary>
    private static (string Name, string ObjectType, Action SetReplace, string? SelfReference)? Describe(Node stmt)
    {
        if (stmt.ViewStmt is { } view)
        {
            return (view.View.Relname, "View", () => view.Replace = true, view.View.Relname);
        }

        if (stmt.CreateFunctionStmt is { } func)
        {
            var name = LastPart(func.Funcname);
            if (name is null) return null;
            var kind = func.IsProcedure ? "Procedure"
                : func.ReturnType is { Setof: true } ? "TableValuedFunction"
                : "ScalarFunction";
            return (name, kind, () => func.Replace = true, null);
        }

        if (stmt.CreateTrigStmt is { } trig)
        {
            return ($"{trig.Relation.Relname}.{trig.Trigname}", "DmlTrigger", () => trig.Replace = true, null);
        }

        return null;
    }

    /// <summary>
    /// The bare name from a possibly schema-qualified identifier list
    /// (<c>CreateFunctionStmt.Funcname</c>) — its last element, mirroring the
    /// single-schema, bare-name convention this whole analysis uses.
    /// </summary>
    private static string? LastPart(Google.Protobuf.Collections.RepeatedField<Node> parts) =>
        parts.Count == 0 ? null : parts[^1].String?.Sval;

    /// <summary>
    /// Every table/view name this statement's tree references, walked
    /// generically over the protobuf AST (the same reflection-driven walk
    /// <see cref="Shadow.SchemaRewriter"/> uses to rewrite schema
    /// qualifiers) — so coverage does not depend on enumerating expression
    /// node types. A function or procedure's body is picked up only when the
    /// SQL-standard <c>BEGIN ATOMIC … END</c> form parses it into real AST
    /// (<c>CreateFunctionStmt.SqlBody</c>); a <c>LANGUAGE plpgsql AS $$…$$</c>
    /// body is an opaque string literal to this grammar and contributes
    /// nothing — best-effort dependency tracking, not exhaustive, and never
    /// gating: a missed dependency costs a stale cached definition until the
    /// object's own file next changes, not an incorrect apply.
    /// </summary>
    private static IEnumerable<string> CollectRangeVarNames(IMessage message)
    {
        if (message.Descriptor.Name == "RangeVar")
        {
            var relname = ((RangeVar)message).Relname;
            if (!string.IsNullOrEmpty(relname)) yield return relname;
        }

        foreach (var field in message.Descriptor.Fields.InDeclarationOrder())
        {
            if (field.FieldType != FieldType.Message) continue;
            var value = field.Accessor.GetValue(message);

            if (field.IsRepeated)
            {
                foreach (var item in ((System.Collections.IEnumerable)value).OfType<IMessage>())
                {
                    foreach (var name in CollectRangeVarNames(item)) yield return name;
                }
            }
            else if (value is IMessage child)
            {
                foreach (var name in CollectRangeVarNames(child)) yield return name;
            }
        }
    }
}
