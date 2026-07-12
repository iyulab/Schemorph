using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Schemorph.Core.Migrations;

/// <summary>A versioned migration file: <c>V0001__description.sql</c>.</summary>
public sealed record MigrationScript(int Version, string FileName, string FilePath, string Checksum)
{
    private static readonly Regex NamePattern =
        new(@"^V(?<version>\d+)__(?<slug>.+)\.sql$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool TryParse(string filePath, out MigrationScript? script)
    {
        script = null;
        var fileName = Path.GetFileName(filePath);
        var match = NamePattern.Match(fileName);
        if (!match.Success) return false;

        script = new MigrationScript(
            int.Parse(match.Groups["version"].Value),
            fileName,
            filePath,
            ComputeChecksum(File.ReadAllText(filePath)));
        return true;
    }

    /// <summary>
    /// SHA-256 over line-ending-normalized content, so the same file checked out
    /// on Windows (CRLF) and Linux (LF) yields the same checksum.
    /// </summary>
    public static string ComputeChecksum(string content) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content.Replace("\r\n", "\n"))));
}
