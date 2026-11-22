using System.Text.Json;
using System.Text.Json.Serialization;

namespace Schemorph.Cli;

/// <summary>
/// Best-effort "a newer version of schemorph is available" notice, for a person at a terminal. The live NuGet lookup
/// runs at most once per <see cref="CheckInterval"/> — the answer is cached on disk, so a normal run pays no network
/// cost — and is bounded by <see cref="HttpTimeout"/> so a slow or offline network never delays the command it runs
/// beside. Every failure (network, disk, parse) is swallowed; this never changes the exit code, and it never writes to
/// stdout. It says nothing when stderr is redirected (a script, a CI job, an agent reading the structured output):
/// those callers read the version from <c>schemorph version</c> and the manifest. Switched off entirely by the
/// <c>SCHEMORPH_NO_UPDATE_CHECK</c> environment variable.
/// </summary>
public static class UpdateNotifier
{
    private const string PackageId = "schemorph";
    /// <summary>The environment variable that switches the notice off.</summary>
    public const string DisableEnvVar = "SCHEMORPH_NO_UPDATE_CHECK";
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);
    private static readonly TimeSpan HttpTimeout = TimeSpan.FromSeconds(2);

    /// <param name="currentVersion">The running tool's version; a <c>+revision</c> suffix is ignored (SemVer build metadata).</param>
    /// <param name="output">Where the notice is written; defaults to <see cref="Console.Error"/>.</param>
    /// <param name="cachePath">The cache file; defaults to the user's local application data.</param>
    /// <param name="fetchLatest">The registry lookup; defaults to the live NuGet one.</param>
    /// <param name="forTerminal">Whether a person is reading stderr; defaults to <c>!Console.IsErrorRedirected</c>.</param>
    public static void CheckAndNotify(
        string currentVersion, TextWriter? output = null, string? cachePath = null,
        Func<string?>? fetchLatest = null, bool? forTerminal = null)
    {
        if (Environment.GetEnvironmentVariable(DisableEnvVar) is not null) return;
        if (!(forTerminal ?? !Console.IsErrorRedirected)) return;

        try
        {
            cachePath ??= GetCachePath();
            var cache = ReadCache(cachePath);
            var latest = cache?.LatestKnownVersion;

            if (cache is null || DateTimeOffset.UtcNow - cache.LastCheckedUtc > CheckInterval)
            {
                var fetched = (fetchLatest ?? FetchLatestVersionFromNuGet)();
                if (fetched is not null)
                {
                    latest = fetched;
                    WriteCache(cachePath, new UpdateCache(DateTimeOffset.UtcNow, fetched));
                }
                // A failed lookup leaves the cache as it was, so the next run tries again instead of
                // parking a stale answer for a full interval.
            }

            if (latest is not null && IsNewer(latest, currentVersion))
            {
                var writer = output ?? Console.Error;
                writer.WriteLine();
                writer.WriteLine($"A new version of schemorph is available: {StripBuildMetadata(currentVersion)} -> {latest}");
                writer.WriteLine("  dotnet tool update -g schemorph");
            }
        }
        catch (Exception ex)
        {
            if (Environment.GetEnvironmentVariable("SCHEMORPH_DEBUG") is not null)
                Console.Error.WriteLine($"[update-check] {ex}");
        }
    }

    /// <summary>Whether <paramref name="latest"/> is a newer release than <paramref name="current"/>. A pre-release is never offered.</summary>
    public static bool IsNewer(string latest, string current) =>
        Version.TryParse(StripBuildMetadata(latest), out var l) &&
        Version.TryParse(StripBuildMetadata(current), out var c) &&
        l > c;

    public static UpdateCache? ReadCache(string path) =>
        File.Exists(path) ? JsonSerializer.Deserialize<UpdateCache>(File.ReadAllText(path)) : null;

    public static void WriteCache(string path, UpdateCache cache)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, JsonSerializer.Serialize(cache));
    }

    private static string StripBuildMetadata(string version) => version.Split('+')[0];

    private static string GetCachePath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "schemorph", "update-check.json");

    private static string? FetchLatestVersionFromNuGet()
    {
        using var http = new HttpClient { Timeout = HttpTimeout };
        var json = http.GetStringAsync($"https://api.nuget.org/v3-flatcontainer/{PackageId}/index.json")
            .GetAwaiter().GetResult();
        var versions = JsonSerializer.Deserialize<NuGetVersionIndex>(json)?.Versions;
        if (versions is null) return null;

        string? latest = null;
        Version? latestParsed = null;
        foreach (var v in versions)
        {
            // Release versions only: `Version.TryParse` refuses a pre-release suffix, so `0.25.0-rc.1` is never "the latest".
            if (!Version.TryParse(v, out var parsed)) continue;
            if (latestParsed is null || parsed > latestParsed)
            {
                latestParsed = parsed;
                latest = v;
            }
        }
        return latest;
    }

    public sealed record UpdateCache(DateTimeOffset LastCheckedUtc, string LatestKnownVersion);

    private sealed class NuGetVersionIndex
    {
        [JsonPropertyName("versions")]
        public List<string>? Versions { get; set; }
    }
}
