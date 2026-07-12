using Schemorph.Core.Providers;
using Schemorph.Provider.SqlServer;

namespace Schemorph.IntegrationTests;

/// <summary>
/// Golden corpus: freezes DacFx SchemaComparison's raw output per scenario — the
/// most plausible regression surface for this tool (engine edge-case behavior,
/// especially across DacFx upgrades; see the version policy in CONTRIBUTING).
/// A scenario is Corpus/&lt;name&gt;/ with optional setup.sql (initial database
/// state), desired/ (desired-state files) and expected.txt (sorted raw changes).
/// A missing expected.txt is bootstrapped from the actual result and the test
/// fails once, forcing review before the baseline freezes.
/// </summary>
public sealed class GoldenCorpusTests : IDisposable
{
    // Read from the source tree (not the output dir) so bootstrap lands in git.
    private static readonly string CorpusRoot =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Corpus"));

    private readonly TestDatabase _db = new();

    public static TheoryData<string> Scenarios()
    {
        var data = new TheoryData<string>();
        foreach (var dir in Directory.GetDirectories(CorpusRoot).OrderBy(d => d, StringComparer.Ordinal))
        {
            data.Add(Path.GetFileName(dir));
        }
        return data;
    }

    [SkippableTheory]
    [MemberData(nameof(Scenarios))]
    public async Task Raw_comparison_output_matches_the_frozen_baseline(string scenario)
    {
        var dir = Path.Combine(CorpusRoot, scenario);
        var provider = new SqlServerProvider();

        var setup = Path.Combine(dir, "setup.sql");
        if (File.Exists(setup))
        {
            await provider.ExecuteScriptAsync(_db.Url, File.ReadAllText(setup));
        }

        var compared = await provider.CompareAsync(new CompareRequest(Path.Combine(dir, "desired"), _db.Url));
        Assert.DoesNotContain(compared.Messages, m => m.Severity == "Error");

        var actual = compared.Changes
            .Select(c => $"{c.Operation} {c.ObjectType} {c.ObjectName}")
            .OrderBy(l => l, StringComparer.Ordinal)
            .ToArray();

        var expectedFile = Path.Combine(dir, "expected.txt");
        if (!File.Exists(expectedFile))
        {
            File.WriteAllLines(expectedFile, actual);
            Assert.Fail($"Baseline bootstrapped at {expectedFile} — review it, then re-run.");
        }

        var expected = File.ReadAllLines(expectedFile)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .OrderBy(l => l, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(expected, actual);
    }

    public void Dispose() => _db.Dispose();
}
