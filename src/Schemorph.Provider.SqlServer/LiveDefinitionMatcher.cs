namespace Schemorph.Provider.SqlServer;

/// <summary>
/// Brownfield reconciliation matching (ADR-0002 addendum): does a desired-state
/// file already say what the live database says? sys.sql_modules preserves the
/// deployed batch text verbatim, so matching is *textual* under only the
/// equivalences Schemorph itself introduces — line endings, outer whitespace,
/// and CREATE OR ALTER vs CREATE (the redefine strategy executes the former,
/// other deployers the latter). Anything subtler must NOT match: a false match
/// would silently adopt a differing object, while a false mismatch merely costs
/// one idempotent redefinition.
/// </summary>
internal static class LiveDefinitionMatcher
{
    /// <summary>True when any batch of the file equals the live definition.</summary>
    internal static bool Matches(string fileText, string liveDefinition)
    {
        var live = Normalize(liveDefinition);
        return SqlBatchSplitter.Split(fileText).Any(batch => Normalize(batch) == live);
    }

    // A trailing semicolon on the batch never changes the semantics of the single
    // statement it terminates — files often carry one, sys.sql_modules often not.
    private static string Normalize(string sql) =>
        SqlServerProvider.NormalizeToCreate(sql).Replace("\r\n", "\n").Trim().TrimEnd(';').TrimEnd();
}
