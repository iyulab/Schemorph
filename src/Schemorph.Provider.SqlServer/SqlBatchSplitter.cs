using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace Schemorph.Provider.SqlServer;

/// <summary>
/// Splits a T-SQL script on GO batch separators (dialect knowledge — GO is a
/// tooling construct, not T-SQL). The parser decides what is a separator, so a
/// GO inside a string literal or comment never splits; the text itself is
/// sliced from the original script, so comments and whitespace stay exactly
/// where they were (LiveDefinitionMatcher compares against sys.sql_modules
/// verbatim — batch text must not be reconstructed). Scripts the parser cannot
/// read (e.g. SQLCMD-mode deployment scripts) fall back to line-based
/// splitting, the pre-parser behavior.
/// </summary>
internal static class SqlBatchSplitter
{
    public static IEnumerable<string> Split(string script)
    {
        var parser = new TSql150Parser(initialQuotedIdentifiers: true);
        using var reader = new StringReader(script);
        var fragment = parser.Parse(reader, out var parseErrors);
        if (fragment?.ScriptTokenStream is null || parseErrors is { Count: > 0 })
        {
            return SplitOnGoLines(script);
        }

        var batches = new List<string>();
        var start = 0;
        foreach (var token in fragment.ScriptTokenStream)
        {
            if (token.TokenType != TSqlTokenType.Go)
            {
                continue;
            }
            Add(batches, script[start..token.Offset]);
            start = token.Offset + token.Text.Length;
        }
        Add(batches, script[start..]);
        return batches;
    }

    private static void Add(List<string> batches, string slice)
    {
        var batch = slice.Trim();
        if (batch.Length > 0) batches.Add(batch);
    }

    /// <summary>The pre-parser fallback: a line that is exactly GO separates.</summary>
    private static IEnumerable<string> SplitOnGoLines(string script)
    {
        var batch = new List<string>();
        foreach (var line in script.Replace("\r\n", "\n").Split('\n'))
        {
            if (line.Trim().Equals("GO", StringComparison.OrdinalIgnoreCase))
            {
                if (batch.Count > 0) yield return string.Join('\n', batch);
                batch.Clear();
            }
            else
            {
                batch.Add(line);
            }
        }

        if (batch.Any(l => l.Trim().Length > 0)) yield return string.Join('\n', batch);
    }
}
