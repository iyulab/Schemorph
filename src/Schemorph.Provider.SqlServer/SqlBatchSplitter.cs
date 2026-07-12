namespace Schemorph.Provider.SqlServer;

/// <summary>
/// Splits a T-SQL script on GO batch separators (dialect knowledge — GO is a
/// tooling construct, not T-SQL). Line-based for now; a ScriptDom-based
/// splitter should replace this before handling adversarial input
/// (GO inside strings/comments).
/// </summary>
internal static class SqlBatchSplitter
{
    public static IEnumerable<string> Split(string script)
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
