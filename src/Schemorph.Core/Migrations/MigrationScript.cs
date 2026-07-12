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

    /// <inheritdoc cref="ContentChecksum.Compute"/>
    public static string ComputeChecksum(string content) => ContentChecksum.Compute(content);
}
