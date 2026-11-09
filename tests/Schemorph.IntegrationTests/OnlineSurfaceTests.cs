using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Client;

namespace Schemorph.IntegrationTests;

/// <summary>
/// The online stage (ADR-0009) as a user meets it over the real binary - the command line and the MCP tools.
/// Without a database: a provider that cannot plan the stage turns the option down before touching anything,
/// and both surfaces advertise it as optional. With a live PostgreSQL server (gated like every live test):
/// the whole review-then-apply loop, and a stage that fails the way a real one does, reported with the
/// stage named and re-run to convergence.
/// </summary>
public sealed class OnlineSurfaceTests : IDisposable
{
    private readonly string _dir = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), $"schemorph-online-{Guid.NewGuid():N}")).FullName;

    private const string NeverOpened = "Server=unreachable.invalid;Database=never;User Id=u;Password=p;Connect Timeout=1";
    private static string? ServerUrl => Environment.GetEnvironmentVariable("SCHEMORPH_PG_TEST_URL");
    private readonly string _schema = "schemorph_online_" + Guid.NewGuid().ToString("n")[..8];

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static string CliDll => Path.Combine(AppContext.BaseDirectory, "schemorph.dll");

    private sealed record CliResult(int ExitCode, string StdOut, string StdErr);

    private static CliResult Run(string arguments, string url, string? provider = null)
    {
        var psi = new ProcessStartInfo("dotnet", $"exec \"{CliDll}\" {arguments}")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.Environment["SCHEMORPH_URL"] = url;
        if (provider is null) psi.Environment.Remove("SCHEMORPH_PROVIDER");
        else psi.Environment["SCHEMORPH_PROVIDER"] = provider;

        using var process = Process.Start(psi)!;
        // stderr is drained concurrently: a child that fills its pipe would block while we wait on stdout alone.
        var stderrTask = process.StandardError.ReadToEndAsync();
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = stderrTask.GetAwaiter().GetResult();
        process.WaitForExit();
        return new CliResult(process.ExitCode, stdout, stderr);
    }

    private static JsonElement ErrorOf(CliResult result)
        => JsonDocument.Parse(string.IsNullOrWhiteSpace(result.StdErr) ? result.StdOut : result.StdErr).RootElement.GetProperty("error");

    // ---- no database

    [Theory]
    [InlineData("diff")]
    [InlineData("apply")]
    public void The_cli_turns_the_option_down_on_a_provider_that_cannot_plan_the_stage(string verb)
    {
        var result = Run($"{verb} --schema \"{_dir}\" --online", NeverOpened);

        Assert.Equal(1, result.ExitCode);
        var error = ErrorOf(result);
        Assert.Equal("not_implemented", error.GetProperty("code").GetString());
        Assert.Equal("unsupported", error.GetProperty("kind").GetString());
        Assert.Contains("online stage", error.GetProperty("message").GetString());
    }

    [Fact]
    public void The_manifest_lists_the_option_on_diff_and_apply()
    {
        var psi = new ProcessStartInfo("dotnet", $"exec \"{CliDll}\" schema")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
        };
        using var process = Process.Start(psi)!;
        var manifest = JsonDocument.Parse(process.StandardOutput.ReadToEnd()).RootElement;
        process.WaitForExit();

        foreach (var verb in new[] { "diff", "apply" })
        {
            var flags = manifest.GetProperty("verbs").EnumerateArray()
                .Single(c => c.GetProperty("name").GetString() == verb)
                .GetProperty("options").EnumerateArray().Select(o => o.GetProperty("flag").GetString());
            Assert.Contains("--online", flags);
        }
    }

    private async Task<McpClient> ConnectAsync(string url, string? provider = null)
    {
        var environment = new Dictionary<string, string?> { ["SCHEMORPH_URL"] = url };
        if (provider is not null) environment["SCHEMORPH_PROVIDER"] = provider;
        return await McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "schemorph",
            Command = "dotnet",
            Arguments = new[] { CliDll, "mcp" },
            EnvironmentVariables = environment,
        }));
    }

    private static JsonElement ErrorOf(ModelContextProtocol.Protocol.CallToolResult result)
    {
        Assert.True(result.IsError);
        var text = Assert.IsType<ModelContextProtocol.Protocol.TextContentBlock>(Assert.Single(result.Content)).Text;
        return JsonDocument.Parse(text).RootElement.GetProperty("error");
    }

    [Theory]
    [InlineData("schemorph_diff")]
    [InlineData("schemorph_apply")]
    public async Task The_mcp_tools_turn_the_option_down_the_same_way_and_advertise_it_as_optional(string tool)
    {
        await using var client = await ConnectAsync(NeverOpened);

        var schema = (await client.ListToolsAsync()).Single(t => t.Name == tool).JsonSchema;
        Assert.True(schema.GetProperty("properties").TryGetProperty("online", out _));
        var required = schema.TryGetProperty("required", out var r)
            ? r.EnumerateArray().Select(x => x.GetString()).ToList()
            : new List<string?>();
        Assert.DoesNotContain("online", required);

        var arguments = new Dictionary<string, object?> { ["schemaDir"] = _dir, ["online"] = true };
        if (tool == "schemorph_apply") arguments["expectedPlanHash"] = new string('0', 64);
        var error = ErrorOf(await client.CallToolAsync(tool, arguments));
        Assert.Equal("not_implemented", error.GetProperty("code").GetString());
        Assert.Contains("online stage", error.GetProperty("message").GetString());
    }

    // ---- a live PostgreSQL server

    private string Url() => ServerUrl!.TrimEnd(';') + $";Search Path={_schema}";

    private string WriteNotes(bool withUniqueName)
    {
        var tables = Directory.CreateDirectory(Path.Combine(_dir, "tables")).FullName;
        File.WriteAllText(Path.Combine(tables, "Notes.sql"), $"""
            CREATE TABLE "{_schema}"."Notes" (
                "Id" integer NOT NULL,
                "Name" text,
                CONSTRAINT "PK_Notes" PRIMARY KEY ("Id")
            );
            {(withUniqueName ? $"""CREATE UNIQUE INDEX "IX_Notes_Name" ON "{_schema}"."Notes" ("Name");""" : "")}
            """);
        return _dir;
    }

    [SkippableFact]
    public void The_reviewed_online_plan_is_applied_after_the_commit_and_a_failed_stage_is_reported_and_re_run_to_convergence()
    {
        Skip.If(ServerUrl is null, "SCHEMORPH_PG_TEST_URL is not set; Postgres CLI tests need a live server.");

        var migrations = Directory.CreateDirectory(Path.Combine(_dir, "migrations")).FullName;
        File.WriteAllText(Path.Combine(migrations, "V1__seed.sql"),
            $"""INSERT INTO "{_schema}"."Notes" ("Id", "Name") VALUES (1, 'dup'), (2, 'dup');""");

        // The table and two rows that share a name, applied the ordinary way.
        WriteNotes(withUniqueName: false);
        Assert.Equal(0, Run($"apply --schema \"{_dir}\" --migrations \"{migrations}\"", Url(), "postgres").ExitCode);

        // Review: the unique index is an online statement, and the plan says so.
        WriteNotes(withUniqueName: true);
        var diff = Run($"diff --schema \"{_dir}\" --migrations \"{migrations}\" --online --format json", Url(), "postgres");
        Assert.Equal(2, diff.ExitCode);
        var plan = JsonDocument.Parse(diff.StdOut).RootElement;
        Assert.Equal("1.12", plan.GetProperty("formatVersion").GetString());
        var step = plan.GetProperty("online").EnumerateArray().Single();
        Assert.StartsWith("CREATE UNIQUE INDEX CONCURRENTLY", step.GetProperty("sql").GetString());
        var hash = plan.GetProperty("planHash").GetString()!;

        // The review without the option is a different plan: its hash does not open an --online apply.
        var plainHash = JsonDocument.Parse(
            Run($"diff --schema \"{_dir}\" --migrations \"{migrations}\" --format json", Url(), "postgres").StdOut)
            .RootElement.GetProperty("planHash").GetString();
        Assert.NotEqual(hash, plainHash);
        var refused = Run($"apply --schema \"{_dir}\" --migrations \"{migrations}\" --online --expect-plan {plainHash}", Url(), "postgres");
        Assert.Contains("plan_mismatch", refused.StdOut + refused.StdErr);

        // The stage fails the way a real one does: the rows share a name. The error names the stage, says the
        // transactional stages are committed, and points at re-running the same apply.
        var failed = Run($"apply --schema \"{_dir}\" --migrations \"{migrations}\" --online --expect-plan {hash} --format json",
            Url(), "postgres");
        Assert.Equal(1, failed.ExitCode);
        var error = ErrorOf(failed);
        Assert.Equal("online_execution_failed", error.GetProperty("code").GetString());
        Assert.Equal("execution", error.GetProperty("kind").GetString());   // the database failed it; not "internal"
        Assert.Equal("online", error.GetProperty("stage").GetString());
        Assert.Equal("23505", error.GetProperty("engine").GetProperty("code").GetString());
        var hint = error.GetProperty("hint").GetString()!;
        Assert.Contains("0 of 1 online statement(s) completed", hint);
        Assert.Contains("re-run the same apply", hint);

        // Fix the data with a migration and run the same command line again: it converges.
        File.WriteAllText(Path.Combine(migrations, "V2__dedupe.sql"), $"""DELETE FROM "{_schema}"."Notes" WHERE "Id" = 2;""");
        var reviewed = JsonDocument.Parse(
            Run($"diff --schema \"{_dir}\" --migrations \"{migrations}\" --online --format json", Url(), "postgres").StdOut)
            .RootElement.GetProperty("planHash").GetString();
        var applied = Run($"apply --schema \"{_dir}\" --migrations \"{migrations}\" --online --expect-plan {reviewed} --format json",
            Url(), "postgres");
        Assert.Equal(0, applied.ExitCode);
        var online = JsonDocument.Parse(applied.StdOut).RootElement.GetProperty("online");
        Assert.True(online.GetProperty("completed").GetInt32() >= 1);
        Assert.Equal(online.GetProperty("total").GetInt32(), online.GetProperty("completed").GetInt32());

        // And nothing is left to plan.
        Assert.Equal(0, Run($"diff --schema \"{_dir}\" --migrations \"{migrations}\" --online", Url(), "postgres").ExitCode);
    }

    [SkippableFact]
    public void The_review_script_puts_the_online_stage_last_and_says_it_is_not_atomic()
    {
        Skip.If(ServerUrl is null, "SCHEMORPH_PG_TEST_URL is not set; Postgres CLI tests need a live server.");

        WriteNotes(withUniqueName: false);
        Assert.Equal(0, Run($"apply --schema \"{_dir}\"", Url(), "postgres").ExitCode);
        WriteNotes(withUniqueName: true);

        var script = Run($"diff --schema \"{_dir}\" --online --format sql", Url(), "postgres");

        Assert.Equal(2, script.ExitCode);
        Assert.Contains("Stage 2 of 2: online (1 statement(s))", script.StdOut);
        Assert.Contains("NOT atomic", script.StdOut);
        Assert.Contains("CREATE UNIQUE INDEX CONCURRENTLY", script.StdOut);
    }
}
