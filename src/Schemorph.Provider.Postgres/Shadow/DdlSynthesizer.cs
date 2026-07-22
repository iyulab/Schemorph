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
    public static IReadOnlyList<string> Synthesize(
        string targetSchema, IReadOnlyList<PgTable> desired, IReadOnlyList<PgTable> live)
    {
        var constraintDrops = new List<string>();
        var tableCreates = new List<string>();
        var columnChanges = new List<string>();
        var constraintAdds = new List<string>();
        var foreignKeyAdds = new List<string>();
        var tableDrops = new List<string>();

        var liveByName = live.ToDictionary(t => t.Name, StringComparer.Ordinal);
        var desiredNames = desired.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);

        foreach (var want in desired)
        {
            var qualified = Qualified(targetSchema, want.Name);

            if (!liveByName.TryGetValue(want.Name, out var have))
            {
                tableCreates.Add(CreateTable(qualified, want));
                foreach (var constraint in want.Constraints)
                {
                    AddConstraint(qualified, constraint, constraintAdds, foreignKeyAdds);
                }
                continue;
            }

            SynthesizeColumns(qualified, want, have, columnChanges);
            SynthesizeConstraints(qualified, want, have, constraintDrops, constraintAdds, foreignKeyAdds);
        }

        foreach (var have in live)
        {
            if (!desiredNames.Contains(have.Name))
            {
                tableDrops.Add($"DROP TABLE {Qualified(targetSchema, have.Name)};");
            }
        }

        return
        [
            .. constraintDrops,
            .. tableCreates,
            .. columnChanges,
            .. constraintAdds,
            .. foreignKeyAdds,
            .. tableDrops,
        ];
    }

    private static void SynthesizeColumns(
        string qualified, PgTable want, PgTable have, List<string> statements)
    {
        var haveByName = have.Columns.ToDictionary(c => c.Name, StringComparer.Ordinal);
        var wantNames = want.Columns.Select(c => c.Name).ToHashSet(StringComparer.Ordinal);

        foreach (var column in want.Columns)
        {
            if (!haveByName.TryGetValue(column.Name, out var existing))
            {
                statements.Add($"ALTER TABLE {qualified} ADD COLUMN {DesiredStateRenderer.RenderColumn(column)};");
                continue;
            }
            if (column == existing) continue;

            var name = DesiredStateRenderer.Quote(column.Name);

            // A generation expression cannot be altered — the column is
            // rebuilt. Honest and visible: it is a drop plus an add.
            if (column.GeneratedAs != existing.GeneratedAs)
            {
                statements.Add($"ALTER TABLE {qualified} DROP COLUMN {name};");
                statements.Add($"ALTER TABLE {qualified} ADD COLUMN {DesiredStateRenderer.RenderColumn(column)};");
                continue;
            }

            if (column.DataType != existing.DataType)
            {
                // No USING clause: where the engine cannot cast, its own error
                // is the honest outcome, not a guessed conversion.
                statements.Add($"ALTER TABLE {qualified} ALTER COLUMN {name} TYPE {column.DataType};");
            }
            if (column.Default != existing.Default)
            {
                statements.Add(column.Default is null
                    ? $"ALTER TABLE {qualified} ALTER COLUMN {name} DROP DEFAULT;"
                    : $"ALTER TABLE {qualified} ALTER COLUMN {name} SET DEFAULT {column.Default};");
            }
            if (column.NotNull != existing.NotNull)
            {
                statements.Add(column.NotNull
                    ? $"ALTER TABLE {qualified} ALTER COLUMN {name} SET NOT NULL;"
                    : $"ALTER TABLE {qualified} ALTER COLUMN {name} DROP NOT NULL;");
            }
            if (column.Identity != existing.Identity || column.IdentityOptions != existing.IdentityOptions)
            {
                if (existing.Identity != PgIdentity.None)
                {
                    statements.Add($"ALTER TABLE {qualified} ALTER COLUMN {name} DROP IDENTITY;");
                }
                if (column.Identity != PgIdentity.None)
                {
                    statements.Add(
                        $"ALTER TABLE {qualified} ALTER COLUMN {name} ADD{DesiredStateRenderer.IdentityClause(column)};");
                }
            }
        }

        foreach (var column in have.Columns)
        {
            if (!wantNames.Contains(column.Name))
            {
                statements.Add($"ALTER TABLE {qualified} DROP COLUMN {DesiredStateRenderer.Quote(column.Name)};");
            }
        }
    }

    private static void SynthesizeConstraints(
        string qualified, PgTable want, PgTable have,
        List<string> drops, List<string> adds, List<string> foreignKeyAdds)
    {
        var haveByName = have.Constraints.ToDictionary(c => c.Name, StringComparer.Ordinal);
        var wantByName = want.Constraints.ToDictionary(c => c.Name, StringComparer.Ordinal);

        foreach (var constraint in have.Constraints)
        {
            var replaced = wantByName.TryGetValue(constraint.Name, out var target)
                && target.Definition != constraint.Definition;
            if (replaced || !wantByName.ContainsKey(constraint.Name))
            {
                drops.Add($"ALTER TABLE {qualified} DROP CONSTRAINT {DesiredStateRenderer.Quote(constraint.Name)};");
            }
        }

        foreach (var constraint in want.Constraints)
        {
            var unchanged = haveByName.TryGetValue(constraint.Name, out var existing)
                && existing.Definition == constraint.Definition;
            if (!unchanged)
            {
                AddConstraint(qualified, constraint, adds, foreignKeyAdds);
            }
        }
    }

    private static void AddConstraint(
        string qualified, PgConstraint constraint, List<string> adds, List<string> foreignKeyAdds)
    {
        var target = constraint.Definition.StartsWith("FOREIGN KEY", StringComparison.Ordinal)
            ? foreignKeyAdds
            : adds;
        target.Add(
            $"ALTER TABLE {qualified} ADD CONSTRAINT {DesiredStateRenderer.Quote(constraint.Name)} {constraint.Definition};");
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
