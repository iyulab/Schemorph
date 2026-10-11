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

    /// <summary>
    /// Manifest 1.7: `diff --migrations` — the pending migrations an apply runs are part of the plan
    /// it is gated on, so the verb that produces the plan has to be able to see them.
    /// </summary>
    [Fact]
    public void Manifest_lists_the_migrations_option_on_diff()
    {
        var manifest = JsonDocument.Parse(Run("schema").StdOut).RootElement;
        var diff = manifest.GetProperty("verbs").EnumerateArray().Single(c => c.GetProperty("name").GetString() == "diff");

        Assert.Contains(diff.GetProperty("options").EnumerateArray(), o => o.GetProperty("flag").GetString() == "--migrations");
    }

    /// <summary>
    /// Manifest 1.8: `--pre` — pre-stage scripts are passed the same way to the verb that plans, the
    /// one that applies and the one that reports, and apply can now end with 2 (work left for the
    /// next run).
    /// </summary>
    [Fact]
    public void Manifest_lists_the_pre_stage_option_and_apply_s_pending_exit_code()
    {
        var manifest = JsonDocument.Parse(Run("schema").StdOut).RootElement;
        var verbs = manifest.GetProperty("verbs").EnumerateArray().ToList();

        foreach (var name in new[] { "diff", "apply", "status" })
        {
            var verb = verbs.Single(c => c.GetProperty("name").GetString() == name);
            Assert.Contains(verb.GetProperty("options").EnumerateArray(), o => o.GetProperty("flag").GetString() == "--pre");
        }
        var apply = verbs.Single(c => c.GetProperty("name").GetString() == "apply");
        Assert.Contains(2, apply.GetProperty("exitCodes").EnumerateArray().Select(e => e.GetInt32()));
    }

    /// <summary>
    /// An option the verb does not take is refused before anything runs — and named, with the nearest
    /// one it does take. Ignoring it would run a different command than the one typed.
    /// </summary>
    [Fact]
    public void An_unknown_option_is_refused_with_the_nearest_known_one()
    {
        var result = Run("diff --format json --schema . --migration ./m", "Server=x");

        Assert.Equal(1, result.ExitCode);
        var error = JsonDocument.Parse(result.StdErr).RootElement.GetProperty("error");
        Assert.Equal("invalid_arguments", error.GetProperty("code").GetString());
        Assert.Equal("usage", error.GetProperty("kind").GetString());
        Assert.Contains("--migration", error.GetProperty("message").GetString());
        Assert.Contains("Did you mean --migrations?", error.GetProperty("hint").GetString());
    }

    /// <summary>The parser's list is the manifest's: every option a verb advertises is accepted.</summary>
    [Fact]
    public void Every_option_the_manifest_lists_is_accepted_by_its_verb()
    {
        var manifest = JsonDocument.Parse(Run("schema").StdOut).RootElement;
        foreach (var verb in manifest.GetProperty("verbs").EnumerateArray())
        {
            var name = verb.GetProperty("name").GetString()!;
            foreach (var option in verb.GetProperty("options").EnumerateArray())
            {
                var flag = option.GetProperty("flag").GetString();
                var takesValue = option.GetProperty("value").ValueKind == JsonValueKind.String;
                // No URL: every verb stops at its own argument checks, after option validation, without
                // reaching for a database.
                var result = Run($"{name} {flag}{(takesValue ? " x" : "")}");
                Assert.DoesNotContain("Unknown option", result.StdErr);
            }
        }
    }

    [Fact]
    public void A_missing_pre_stage_directory_is_named()
    {
        var result = Run("diff --format json --schema . --pre ./no-such-pre-dir", "Server=x");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("pre_dir_not_found", result.StdErr);
    }

    /// <summary>
    /// The two-run pre-stage end to end on the CLI: diff says work is pending, the gated apply runs the
    /// pre-stage only and says so with 2, and the second round converges to 0.
    /// </summary>
    [SkippableFact]
    public void A_deferred_apply_exits_2_and_the_next_round_converges()
    {
        using var db = new TestDatabase();
        var schema = Directory.CreateDirectory(Path.Combine(_dir, "schema", "tables")).Parent!.FullName;
        var pre = Directory.CreateDirectory(Path.Combine(_dir, "pre")).FullName;
        db.Execute("CREATE TABLE dbo.T (Id INT NOT NULL PRIMARY KEY, Notes NVARCHAR(50) NULL)");
        db.Execute("INSERT INTO dbo.T VALUES (1, N'keep me')");
        File.WriteAllText(Path.Combine(schema, "tables", "dbo.T.sql"),
            "CREATE TABLE dbo.T (Id INT NOT NULL PRIMARY KEY, Remarks NVARCHAR(50) NULL);\nGO\n");
        File.WriteAllText(Path.Combine(pre, "V1__rename.sql"), "EXEC sp_rename 'dbo.T.Notes', 'Remarks', 'COLUMN';");

        var diff = Run($"diff --schema \"{schema}\" --pre \"{pre}\"", db.Url);
        Assert.Equal(2, diff.ExitCode);
        var hash = JsonDocument.Parse(diff.StdOut).RootElement.GetProperty("planHash").GetString();

        var apply = Run($"apply --schema \"{schema}\" --pre \"{pre}\" --expect-plan {hash}", db.Url);
        Assert.Equal(2, apply.ExitCode);
        var applied = JsonDocument.Parse(apply.StdOut).RootElement;
        Assert.True(applied.GetProperty("deferred").GetBoolean());
        Assert.Equal("V1__rename.sql", applied.GetProperty("pre").GetProperty("applied")[0].GetString());

        var again = Run($"diff --schema \"{schema}\" --pre \"{pre}\"", db.Url);
        Assert.Equal(0, again.ExitCode);
        Assert.Equal("keep me", db.Scalar<string>("SELECT Remarks FROM dbo.T"));
    }

    /// <summary>
    /// `status --pre`: a pre-stage script waiting to run is pending work — exit 2, named in the output — and once
    /// the apply has run it, status is 0 with the same directory passed.
    /// </summary>
    [SkippableFact]
    public void Status_counts_a_waiting_pre_stage_script_as_pending_work()
    {
        using var db = new TestDatabase();
        var schema = Directory.CreateDirectory(Path.Combine(_dir, "schema", "tables")).Parent!.FullName;
        var pre = Directory.CreateDirectory(Path.Combine(_dir, "pre")).FullName;
        db.Execute("CREATE TABLE dbo.T (Id INT NOT NULL PRIMARY KEY, Notes NVARCHAR(50) NULL)");
        File.WriteAllText(Path.Combine(schema, "tables", "dbo.T.sql"),
            "CREATE TABLE dbo.T (Id INT NOT NULL PRIMARY KEY, Remarks NVARCHAR(50) NULL);\nGO\n");
        File.WriteAllText(Path.Combine(pre, "V1__rename.sql"), "EXEC sp_rename 'dbo.T.Notes', 'Remarks', 'COLUMN';");

        var waiting = Run($"status --schema \"{schema}\" --pre \"{pre}\"", db.Url);
        Assert.Equal(2, waiting.ExitCode);
        Assert.Contains("V1__rename.sql", waiting.StdOut);

        var diff = Run($"diff --schema \"{schema}\" --pre \"{pre}\"", db.Url);
        var hash = JsonDocument.Parse(diff.StdOut).RootElement.GetProperty("planHash").GetString();
        Assert.Equal(2, Run($"apply --schema \"{schema}\" --pre \"{pre}\" --expect-plan {hash}", db.Url).ExitCode);

        var settled = Run($"status --schema \"{schema}\" --pre \"{pre}\"", db.Url);
        Assert.Equal(0, settled.ExitCode);
        Assert.DoesNotContain("V1__rename.sql", JsonDocument.Parse(settled.StdOut).RootElement
            .GetProperty("plan").GetProperty("pre").ToString());
    }

    /// <summary>
    /// Manifest 1.6: `desiredStateFormatVersion` versions the `inspect`/`schemorph://schema`
    /// file-tree rendering contract (docs/desired-state-format.md), independently of
    /// `planFormatVersion` — this is what lets a consumer diffing its own copy of rendered
    /// output against a fresh inspect tell "the schema drifted" apart from "the rendering
    /// rules changed across an upgrade".
    /// </summary>
    [Fact]
    public void Manifest_carries_the_desired_state_format_version()
    {
        var manifest = JsonDocument.Parse(Run("schema").StdOut).RootElement;

        Assert.Equal("1.8", manifest.GetProperty("manifestVersion").GetString());
        Assert.Equal("1.0", manifest.GetProperty("desiredStateFormatVersion").GetString());
        Assert.Equal("docs/desired-state-format.md", manifest.GetProperty("docs").GetProperty("desiredStateFormat").GetString());
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
