using Schemorph.Cli;

namespace Schemorph.IntegrationTests;

/// <summary>
/// The "a newer version is available" notice: said once to a person at a terminal, cached, silent for everything else. None
/// of these need a database, and none touch the network — the lookup is handed in.
/// </summary>
public sealed class UpdateNoticeTests : IDisposable
{
    private readonly string _dir = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), $"schemorph-update-{Guid.NewGuid():N}")).FullName;
    private readonly string? _envBefore = Environment.GetEnvironmentVariable(UpdateNotifier.DisableEnvVar);

    public UpdateNoticeTests() => Environment.SetEnvironmentVariable(UpdateNotifier.DisableEnvVar, null);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(UpdateNotifier.DisableEnvVar, _envBefore);
        Directory.Delete(_dir, recursive: true);
    }

    private string Cache => Path.Combine(_dir, "nested", "update-check.json");

    private sealed class Lookup(string? answer, bool throws = false)
    {
        public int Calls;

        public string? Fetch()
        {
            Calls++;
            if (throws) throw new HttpRequestException("offline");
            return answer;
        }
    }

    [Theory]
    [InlineData("0.25.0", "0.24.0", true)]
    [InlineData("0.24.0", "0.24.0", false)]
    [InlineData("0.23.0", "0.24.0", false)]
    [InlineData("0.25.0", "0.24.0+3f2a1bc", true)]   // build metadata does not count
    [InlineData("0.24.0+abc", "0.24.0", false)]
    [InlineData("0.25.0-rc.1", "0.24.0", false)]      // a pre-release is never offered
    [InlineData("0.25.0", "0.1.0-dev", false)]        // a local build says nothing comparable
    [InlineData("nonsense", "0.24.0", false)]
    public void A_version_is_newer_only_when_it_is_a_later_release(string latest, string current, bool expected)
        => Assert.Equal(expected, UpdateNotifier.IsNewer(latest, current));

    [Fact]
    public void A_newer_release_is_announced_on_the_writer_once_the_answer_is_cached()
    {
        var lookup = new Lookup("0.25.0");
        var first = new StringWriter();
        var second = new StringWriter();

        UpdateNotifier.CheckAndNotify("0.24.0+abc123", first, Cache, lookup.Fetch, forTerminal: true);
        UpdateNotifier.CheckAndNotify("0.24.0+abc123", second, Cache, lookup.Fetch, forTerminal: true);

        Assert.Contains("0.24.0 -> 0.25.0", first.ToString());
        Assert.Contains("dotnet tool update -g schemorph", first.ToString());
        Assert.Equal(first.ToString(), second.ToString());   // still said, from the cache
        Assert.Equal(1, lookup.Calls);                       // but the registry was asked once
    }

    [Fact]
    public void Nothing_is_said_or_asked_when_stderr_is_not_a_terminal()
    {
        var lookup = new Lookup("0.25.0");
        var writer = new StringWriter();

        UpdateNotifier.CheckAndNotify("0.24.0", writer, Cache, lookup.Fetch, forTerminal: false);

        Assert.Equal("", writer.ToString());
        Assert.Equal(0, lookup.Calls);
        Assert.False(File.Exists(Cache));
    }

    [Fact]
    public void The_environment_variable_turns_it_off_entirely()
    {
        Environment.SetEnvironmentVariable(UpdateNotifier.DisableEnvVar, "1");
        var lookup = new Lookup("0.25.0");
        var writer = new StringWriter();

        UpdateNotifier.CheckAndNotify("0.24.0", writer, Cache, lookup.Fetch, forTerminal: true);

        Assert.Equal("", writer.ToString());
        Assert.Equal(0, lookup.Calls);
    }

    [Fact]
    public void Being_up_to_date_says_nothing()
    {
        var writer = new StringWriter();

        UpdateNotifier.CheckAndNotify("0.25.0", writer, Cache, new Lookup("0.25.0").Fetch, forTerminal: true);

        Assert.Equal("", writer.ToString());
    }

    [Theory]
    [InlineData(false)]   // the answer is missing
    [InlineData(true)]    // the lookup throws
    public void A_failed_lookup_says_nothing_leaves_no_cache_and_is_asked_again_next_time(bool throws)
    {
        var lookup = new Lookup(null, throws);
        var writer = new StringWriter();

        UpdateNotifier.CheckAndNotify("0.24.0", writer, Cache, lookup.Fetch, forTerminal: true);
        UpdateNotifier.CheckAndNotify("0.24.0", writer, Cache, lookup.Fetch, forTerminal: true);

        Assert.Equal("", writer.ToString());
        Assert.False(File.Exists(Cache));
        Assert.Equal(2, lookup.Calls);
    }

    [Fact]
    public void A_day_old_answer_is_looked_up_again()
    {
        UpdateNotifier.WriteCache(Cache, new UpdateNotifier.UpdateCache(DateTimeOffset.UtcNow.AddHours(-25), "0.24.5"));
        var lookup = new Lookup("0.26.0");
        var writer = new StringWriter();

        UpdateNotifier.CheckAndNotify("0.24.0", writer, Cache, lookup.Fetch, forTerminal: true);

        Assert.Equal(1, lookup.Calls);
        Assert.Contains("0.24.0 -> 0.26.0", writer.ToString());
    }

    [Fact]
    public void An_unreadable_cache_never_reaches_the_command_it_runs_beside()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Cache)!);
        File.WriteAllText(Cache, "{ not json");
        var writer = new StringWriter();

        var ex = Record.Exception(() => UpdateNotifier.CheckAndNotify("0.24.0", writer, Cache, new Lookup("0.25.0").Fetch, forTerminal: true));

        Assert.Null(ex);
        Assert.Equal("", writer.ToString());
    }
}
