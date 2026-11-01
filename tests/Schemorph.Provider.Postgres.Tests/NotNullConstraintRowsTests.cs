namespace Schemorph.Provider.Postgres.Tests;

/// <summary>
/// PostgreSQL 18 stores a column's NOT NULL in <c>pg_constraint</c> as well as in
/// <c>attnotnull</c>. The column already carries the flag, so the constraint list must not
/// carry the row too. This pins the query text; reading a server is what proves it.
/// </summary>
public class NotNullConstraintRowsTests
{
    [Fact]
    public void The_constraint_query_leaves_out_not_null_rows()
    {
        Assert.Contains("con.contype <> 'n'", CatalogReader.ConstraintsSql);
    }

    [Fact]
    public void The_constraint_query_still_reads_every_other_kind_of_constraint()
    {
        // Narrowing by exclusion keeps check, foreign key, primary key, unique and exclusion
        // constraints: none of them is named in the filter.
        foreach (var kind in new[] { "'c'", "'f'", "'p'", "'u'", "'x'" })
        {
            Assert.DoesNotContain(kind, CatalogReader.ConstraintsSql);
        }
    }
}
