using System.Text;
using Schemorph.Core.Providers;

namespace Schemorph.Provider.Postgres.Shadow;

/// <summary>
/// Renders the ALTER/CREATE/DROP statements that carry the live schema to the
/// desired one — the layer ADR-0007 named the provider's most expensive
/// unknown, and the point where its §6-style withdrawal condition is tested:
/// synthesis is proven by applying its output and re-diffing to empty, never
/// by reading it.
///
/// Both snapshots come from comparison-mode reads, so embedded expression
/// texts reference same-schema objects UNQUALIFIED; the synthesized script
/// must therefore run with search_path set to the target schema (the executor
/// owns that), while the statements' own table names are always qualified.
///
/// Statement order mirrors the rewriter's dependency classes: constraint
/// drops release columns, tables exist before columns move, foreign keys come
/// after every non-FK constraint (their unique targets), drops of whole
/// tables come last.
/// </summary>
internal static class DdlSynthesizer
{
    /// <summary>
    /// One statement and the table it carries. Every statement belongs to exactly
    /// one table, which is what lets the caller check its own work: a change the
    /// comparison reported and synthesis produced no statement for is an internal
    /// disagreement, and the apply must not report it as done.
    /// <para>
    /// <c>LosesData</c> marks the one statement shape whose rows nothing
    /// recomputes: dropping a column the desired state no longer declares. It is set
    /// where that decision is made, from the model, so the column a generation
    /// expression re-creates (also a <c>DROP COLUMN</c> in the text) is not marked —
    /// its new values are the expression's output. The mark is what lets a gated drop
    /// withhold only itself instead of its whole table's change.
    /// </para>
    /// </summary>
    public sealed record Statement(string ObjectName, string Sql, bool LosesData = false);

    public static IReadOnlyList<Statement> Synthesize(
        string targetSchema, IReadOnlyList<PgTable> desired, IReadOnlyList<PgTable> live)
    {
        var constraintDrops = new List<Statement>();
        var indexDrops = new List<Statement>();
        var tableCreates = new List<Statement>();
        var columnChanges = new List<Statement>();
        var constraintAdds = new List<Statement>();
        var indexCreates = new List<Statement>();
        var foreignKeyAdds = new List<Statement>();
        var tableDrops = new List<Statement>();

        var liveByName = live.ToDictionary(t => t.Name, StringComparer.Ordinal);
        var desiredNames = desired.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);

        foreach (var want in desired)
        {
            var qualified = Qualified(targetSchema, want.Name);

            if (!liveByName.TryGetValue(want.Name, out var have))
            {
                tableCreates.Add(new Statement(want.Name, CreateTable(qualified, want)));
                foreach (var constraint in want.Constraints)
                {
                    AddConstraint(want.Name, qualified, constraint, constraintAdds, foreignKeyAdds);
                }
                foreach (var index in want.Indexes)
                {
                    indexCreates.Add(CreateIndex(want.Name, index));
                }
                continue;
            }

            SynthesizeColumns(qualified, want, have, columnChanges);
            SynthesizeConstraints(qualified, want, have, constraintDrops, constraintAdds, foreignKeyAdds);
            SynthesizeIndexes(targetSchema, want, have, indexDrops, indexCreates);
        }

        foreach (var have in live)
        {
            if (!desiredNames.Contains(have.Name))
            {
                tableDrops.Add(new Statement(
                    have.Name, $"DROP TABLE {Qualified(targetSchema, have.Name)};"));
            }
        }

        return
        [
            .. constraintDrops,
            .. indexDrops,
            .. tableCreates,
            .. columnChanges,
            .. constraintAdds,
            .. indexCreates,
            .. foreignKeyAdds,
            .. tableDrops,
        ];
    }

    private static void SynthesizeColumns(
        string qualified, PgTable want, PgTable have, List<Statement> statements)
    {
        var haveByName = have.Columns.ToDictionary(c => c.Name, StringComparer.Ordinal);
        var wantNames = want.Columns.Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
        void Add(string sql) => statements.Add(new Statement(want.Name, sql));

        foreach (var column in want.Columns)
        {
            if (!haveByName.TryGetValue(column.Name, out var existing))
            {
                Add($"ALTER TABLE {qualified} ADD COLUMN {DesiredStateRenderer.RenderColumn(column)};");
                continue;
            }
            if (column == existing) continue;

            var name = DesiredStateRenderer.Quote(column.Name);

            // The two directions of a generation-expression change are not
            // symmetric, so they cannot share one statement.
            //
            // Dropping the expression keeps every value: the engine turns the
            // column into an ordinary one in place. Rebuilding it instead would
            // discard data — and unlike a generated column's contents, those
            // values are no longer derivable afterwards, because the expression
            // that produced them is precisely what the desired state removed.
            //
            // Gaining or changing an expression has no in-place form on the
            // supported baseline (no SET EXPRESSION before PostgreSQL 17), so the
            // column is dropped and added. That is honest: the new values are the
            // expression's output by definition. The same goes for the same
            // expression kept the other way (stored <-> virtual): there is no
            // in-place conversion, and the values are derivable either way.
            if (column.GeneratedAs != existing.GeneratedAs
                || column.GeneratedAs is not null && column.GeneratedKind != existing.GeneratedKind)
            {
                if (column.GeneratedAs is null)
                {
                    Add($"ALTER TABLE {qualified} ALTER COLUMN {name} DROP EXPRESSION;");
                    // Fall through: a type, default or NOT NULL difference on the
                    // same column still needs its own statement.
                }
                else
                {
                    Add($"ALTER TABLE {qualified} DROP COLUMN {name};");
                    Add($"ALTER TABLE {qualified} ADD COLUMN {DesiredStateRenderer.RenderColumn(column)};");
                    continue;
                }
            }

            if (column.DataType != existing.DataType)
            {
                // No USING clause: where the engine cannot cast, its own error
                // is the honest outcome, not a guessed conversion.
                Add($"ALTER TABLE {qualified} ALTER COLUMN {name} TYPE {column.DataType};");
            }
            if (column.Default != existing.Default)
            {
                Add(column.Default is null
                    ? $"ALTER TABLE {qualified} ALTER COLUMN {name} DROP DEFAULT;"
                    : $"ALTER TABLE {qualified} ALTER COLUMN {name} SET DEFAULT {column.Default};");
            }
            if (column.NotNull != existing.NotNull)
            {
                Add(column.NotNull
                    ? $"ALTER TABLE {qualified} ALTER COLUMN {name} SET NOT NULL;"
                    : $"ALTER TABLE {qualified} ALTER COLUMN {name} DROP NOT NULL;");
            }
            if (column.Identity != existing.Identity || column.IdentityOptions != existing.IdentityOptions)
            {
                if (existing.Identity != PgIdentity.None)
                {
                    Add($"ALTER TABLE {qualified} ALTER COLUMN {name} DROP IDENTITY;");
                }
                if (column.Identity != PgIdentity.None)
                {
                    Add(
                        $"ALTER TABLE {qualified} ALTER COLUMN {name} ADD{DesiredStateRenderer.IdentityClause(column)};");
                }
            }
        }

        foreach (var column in have.Columns)
        {
            if (!wantNames.Contains(column.Name))
            {
                statements.Add(new Statement(want.Name,
                    $"ALTER TABLE {qualified} DROP COLUMN {DesiredStateRenderer.Quote(column.Name)};",
                    LosesData: true));
            }
        }
    }

    private static void SynthesizeConstraints(
        string qualified, PgTable want, PgTable have,
        List<Statement> drops, List<Statement> adds, List<Statement> foreignKeyAdds)
    {
        var haveByName = have.Constraints.ToDictionary(c => c.Name, StringComparer.Ordinal);
        var wantByName = want.Constraints.ToDictionary(c => c.Name, StringComparer.Ordinal);

        foreach (var constraint in have.Constraints)
        {
            var replaced = wantByName.TryGetValue(constraint.Name, out var target)
                && target.Definition != constraint.Definition
                && !AwaitsValidation(target, constraint);
            if (replaced || !wantByName.ContainsKey(constraint.Name))
            {
                drops.Add(new Statement(want.Name,
                    $"ALTER TABLE {qualified} DROP CONSTRAINT {DesiredStateRenderer.Quote(constraint.Name)};"));
            }
        }

        foreach (var constraint in want.Constraints)
        {
            var exists = haveByName.TryGetValue(constraint.Name, out var existing);
            if (exists && existing!.Definition == constraint.Definition) continue;

            if (exists && AwaitsValidation(constraint, existing!))
            {
                // Only the validation is missing: the constraint is in place and enforced for new
                // rows. Dropping and re-adding it would redo the add under the stronger lock and
                // lose the constraint's identity; VALIDATE CONSTRAINT checks the existing rows only.
                (constraint.Definition.StartsWith("FOREIGN KEY", StringComparison.Ordinal) ? foreignKeyAdds : adds)
                    .Add(new Statement(want.Name,
                        $"ALTER TABLE {qualified} VALIDATE CONSTRAINT {DesiredStateRenderer.Quote(constraint.Name)};"));
                continue;
            }

            AddConstraint(want.Name, qualified, constraint, adds, foreignKeyAdds);
        }
    }

    /// <summary>
    /// The live constraint is the declared one, added <c>NOT VALID</c> and never validated.
    /// <c>pg_get_constraintdef</c> ends such a definition with <c>NOT VALID</c> and is otherwise
    /// identical, which is how it is recognized without a parser.
    /// </summary>
    private static bool AwaitsValidation(PgConstraint wanted, PgConstraint live)
        => live.Definition == wanted.Definition + " NOT VALID";

    /// <summary>
    /// Indexes have no ALTER that changes what they index, so a definition
    /// difference is a drop and a create — and a rename is one too, because the
    /// name is part of what the desired state declares and nothing carries the
    /// old index's identity to the new one.
    ///
    /// Nothing is emitted for a table the desired state no longer declares: its
    /// indexes go with the DROP TABLE, and naming them again would drop what is
    /// already gone.
    /// </summary>
    private static void SynthesizeIndexes(
        string targetSchema, PgTable want, PgTable have,
        List<Statement> drops, List<Statement> creates)
    {
        var haveByName = have.Indexes.ToDictionary(i => i.Name, StringComparer.Ordinal);
        var wantByName = want.Indexes.ToDictionary(i => i.Name, StringComparer.Ordinal);

        foreach (var index in have.Indexes)
        {
            // An invalid index (a failed concurrent build) is rebuilt: the engine ignores it, so
            // keeping it would leave the declared index missing while the schema reads as in sync.
            var replaced = wantByName.TryGetValue(index.Name, out var target)
                && (target.Definition != index.Definition || !index.IsValid);
            if (replaced || !wantByName.ContainsKey(index.Name))
            {
                drops.Add(new Statement(want.Name,
                    $"DROP INDEX {Qualified(targetSchema, index.Name)};"));
            }
        }

        foreach (var index in want.Indexes)
        {
            var unchanged = haveByName.TryGetValue(index.Name, out var existing)
                && existing.Definition == index.Definition && existing.IsValid;
            if (!unchanged)
            {
                creates.Add(CreateIndex(want.Name, index));
            }
        }
    }

    // The definition is the engine's own CREATE INDEX with the schema removed
    // from its ON clause, so it lands in the target schema by the search_path
    // the executor sets — the same contract the constraint definitions run under.
    private static Statement CreateIndex(string objectName, PgIndex index)
        => new(objectName, $"{index.Definition.TrimEnd(';', ' ', '\r', '\n')};");

    private static void AddConstraint(
        string objectName, string qualified, PgConstraint constraint,
        List<Statement> adds, List<Statement> foreignKeyAdds)
    {
        var target = constraint.Definition.StartsWith("FOREIGN KEY", StringComparison.Ordinal)
            ? foreignKeyAdds
            : adds;
        target.Add(new Statement(objectName,
            $"ALTER TABLE {qualified} ADD CONSTRAINT {DesiredStateRenderer.Quote(constraint.Name)} {constraint.Definition};"));
    }

    private static string CreateTable(string qualified, PgTable table)
    {
        var sql = new StringBuilder("CREATE TABLE ").Append(qualified).Append(" (");
        for (var i = 0; i < table.Columns.Count; i++)
        {
            if (i > 0) sql.Append(", ");
            sql.Append(DesiredStateRenderer.RenderColumn(table.Columns[i]));
        }
        return sql.Append(");").ToString();
    }

    private static string Qualified(string schema, string table)
        => $"{DesiredStateRenderer.Quote(schema)}.{DesiredStateRenderer.Quote(table)}";
}
