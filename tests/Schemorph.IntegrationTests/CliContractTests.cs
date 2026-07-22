using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Schemorph.IntegrationTests;

/// <summary>
/// The CLI's machine contracts, verified against the real binary: the `schema`
/// manifest (and its dual-maintenance pact with `help`), the non-TTY JSON
/// default, and the semantic exit codes. This is the CLI half of the agent
/// harness — AgentSurfaceTests covers the MCP half.
/// </summary>
public sealed class CliContractTests : IDisposable
{
    private readonly string _dir = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), $"schemorph-cli-{Guid.NewGuid():N}")).FullName;

    private static string CliDll => Path.Combine(AppContext.BaseDirectory, "schemorph.dll");

    private sealed record CliResult(int ExitCode, string StdOut, string StdErr);

    /// <summary>Runs the CLI with redirected stdout/stderr — i.e. exactly the non-TTY case.</summary>
    private static CliResult Run(string arguments, string? url = null)
    {
        var psi = new ProcessStartInfo("dotnet", $"exec \"{CliDll}\" {arguments}")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.Environment.Remove("SCHEMORPH_URL");
        if (url is not null) psi.Environment["SCHEMORPH_URL"] = url;

        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return new CliResult(process.ExitCode, stdout, stderr);
    }

    [Fact]
    public void Manifest_and_help_describe_the_same_verbs()
    {
        var manifest = Run("schema");
        Assert.Equal(0, manifest.ExitCode);
        var verbs = JsonDocument.Parse(manifest.StdOut).RootElement
            .GetProperty("verbs").EnumerateArray()
            .Select(v => v.GetProperty("name").GetString()!)
            .ToList();

        var help = Run("help");
        Assert.Equal(0, help.ExitCode);

        // The dual-maintenance pact (a new verb must land in BOTH): the verb
        // lines in help ("  <verb>   <summary>") and the manifest agree exactly.
        var helpVerbs = System.Text.RegularExpressions.Regex
            .Matches(help.StdOut, @"(?m)^\s{2}(\w[\w-]*)\s{2,}\S")
            .Select(m => m.Groups[1].Value)
            .ToHashSet();
        Assert.Equal(verbs.Order().ToArray(), helpVerbs.Order().ToArray());
    }

    [Fact]
    public void Manifest_flags_include_the_load_bearing_contract_points()
    {
        var manifest = JsonDocument.Parse(Run("schema").StdOut).RootElement;
        var byName = manifest.GetProperty("verbs").EnumerateArray()
            .ToDictionary(v => v.GetProperty("name").GetString()!);

        string[] FlagsOf(string verb) => byName[verb].GetProperty("options").EnumerateArray()
            .Select(o => o.GetProperty("flag").GetString()!).ToArray();

        Assert.Contains("--expect-plan", FlagsOf("apply"));          // the apply gate
        Assert.Contains("--allow-destructive", FlagsOf("diff"));     // the destructive gate
        Assert.Equal(new[] { 0, 1, 2 }, byName["diff"].GetProperty("exitCodes")
            .EnumerateArray().Select(e => e.GetInt32()).ToArray());  // detailed exit codes
        Assert.Equal(new[] { 0, 1, 2 }, byName["status"].GetProperty("exitCodes")
            .EnumerateArray().Select(e => e.GetInt32()).ToArray());
    }

    [Fact]
    public void Manifest_carries_the_provider_declaration()
    {
        // Manifest 1.4: the provider block is the canonical capability layer
        // (dev plan §2) — sourced from the provider's own declaration, so what
        // the manifest claims and what the provider refuses cannot drift apart.
        var provider = JsonDocument.Parse(Run("schema").StdOut).RootElement
            .GetProperty("provider");

        Assert.Equal("sqlserver", provider.GetProperty("name").GetString());
        Assert.Equal("partial", provider.GetProperty("atomicity").GetString());
        var capabilities = provider.GetProperty("capabilities").EnumerateArray()
            .Select(c => c.GetString()).ToArray();
        Assert.Contains("tables", capabilities);
        Assert.Contains("migrations", capabilities);
    }

    [Fact]
    public void Redirected_stdout_defaults_to_the_json_error_envelope()
    {
        // No --format given; stdout is redirected (this process), so the
        // non-TTY heuristic must choose JSON — one envelope on stderr.
        var result = Run("diff");

        Assert.Equal(1, result.ExitCode);
        var error = JsonDocument.Parse(result.StdErr).RootElement.GetProperty("error");
        Assert.Equal("invalid_arguments", error.GetProperty("code").GetString());
        Assert.Equal("usage", error.GetProperty("kind").GetString());
        Assert.False(string.IsNullOrWhiteSpace(error.GetProperty("hint").GetString()));
    }

    [Fact]
    public void Explicit_text_format_renders_the_text_error_shape()
    {
        var result = Run("diff --format text");

        Assert.Equal(1, result.ExitCode);
        Assert.StartsWith("error[invalid_arguments]:", result.StdErr.TrimStart());
    }

    [Fact]
    public void Version_and_unknown_verb_exit_semantically()
    {
        Assert.Equal(0, Run("version").ExitCode);
        Assert.Equal(1, Run("frobnicate").ExitCode);
    }

    [SkippableFact]
    public void Diff_exit_code_tracks_pending_changes_end_to_end()
    {
        using var db = new TestDatabase();
        var schema = Directory.CreateDirectory(Path.Combine(_dir, "tables")).Parent!.FullName;
        File.WriteAllText(Path.Combine(_dir, "tables", "dbo.T.sql"),
            "CREATE TABLE dbo.T (Id INT NOT NULL PRIMARY KEY);\nGO\n");

        // Pending change → exit 2, and the plan JSON lands on stdout.
        var pending = Run($"diff --schema \"{schema}\"", db.Url);
        Assert.Equal(2, pending.ExitCode);
        var plan = JsonDocument.Parse(pending.StdOut).RootElement;
        Assert.True(plan.GetProperty("hasChanges").GetBoolean());
        Assert.False(string.IsNullOrEmpty(plan.GetProperty("planHash").GetString()));

        // Converged → exit 0.
        db.Execute("CREATE TABLE dbo.T (Id INT NOT NULL PRIMARY KEY)");
        var converged = Run($"diff --schema \"{schema}\"", db.Url);
        Assert.Equal(0, converged.ExitCode);
        Assert.False(JsonDocument.Parse(converged.StdOut).RootElement.GetProperty("hasChanges").GetBoolean());
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }
}
