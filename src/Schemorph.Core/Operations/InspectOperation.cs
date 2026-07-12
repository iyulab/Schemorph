using Schemorph.Core.Providers;

namespace Schemorph.Core.Operations;

/// <summary>
/// The inspect orchestration: provider renders the live database as
/// desired-state files, this operation persists them to disk. The rendering
/// itself stays in the provider (dialect knowledge); the disk sink lives here
/// so other surfaces (MCP resources) can consume the same rendering in memory.
/// </summary>
public static class InspectOperation
{
    public sealed record Result(IReadOnlyList<string> WrittenFiles);

    public static async Task<Result> RunAsync(
        IDatabaseProvider provider, string connectionString, string outputDirectory,
        CancellationToken cancellationToken = default)
    {
        var inspected = await provider.InspectAsync(new InspectRequest(connectionString), cancellationToken);

        var written = new List<string>(inspected.Files.Count);
        foreach (var file in inspected.Files)
        {
            // RelativePath is canonically '/'-separated (resource URIs); the disk sink localizes it.
            var path = Path.Combine(outputDirectory, file.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, file.Content);
            written.Add(path);
        }

        return new Result(written);
    }
}
