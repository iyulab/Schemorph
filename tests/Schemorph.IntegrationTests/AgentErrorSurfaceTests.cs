using System.Text.Json;
using ModelContextProtocol.Client;

namespace Schemorph.IntegrationTests;

/// <summary>
/// What an agent sees when something goes wrong, over the REAL MCP stdio surface.
///
/// <see cref="AgentSurfaceTests"/> covers the happy path and the apply gate. This
/// covers the other half, which had been written twice and never exercised: the
/// CLI and the MCP server map failures independently, so "the same envelope
/// everywhere" is a claim, not a fact, until something drives both. Two mappings
/// were shipped in that state — the apply stage/committed fields and the
/// temporary-workspace error — and this is what closes that.
/// </summary>
public sealed class AgentErrorSurfaceTests : IDisposable
{
    private readonly TestDatabase _db = new();
    private readonly string _dir = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), $"schemorph-agenterr-{Guid.NewGuid():N}")).FullName;

    private string SchemaDir => Path.Combine(_dir, "schema");
    private string MigrationsDir => Path.Combine(_dir, "migrations");

    private static string CliDll => Path.Combine(AppContext.BaseDirectory, "schemorph.dll");

    private void Write(string relativePath, string content)
    {
        var path = Path.Combine(_dir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private async Task<McpClient> ConnectAsync(string? tempOverride = null)
    {
        var env = new Dictionary<string, string?> { ["SCHEMORPH_URL"] = _db.Url };
        if (tempOverride is not null)
        {
            env["TMP"] = tempOverride;
            env["TEMP"] = tempOverride;
        }

        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "schemorph",
            Command = "dotnet",
            Arguments = new[] { CliDll, "mcp" },
            EnvironmentVariables = env,
        });
        return await McpClient.CreateAsync(transport);
    }

    private static JsonElement Parse(ModelContextProtocol.Protocol.CallToolResult result)
    {
        var text = Assert.IsType<ModelContextProtocol.Protocol.TextContentBlock>(Assert.Single(result.Content)).Text;
        return JsonDocument.Parse(text).RootElement;
    }

    [SkippableFact]
    public async Task A_failed_apply_names_its_stage_and_what_committed()
    {
        Write("schema/tables/dbo.Orders.sql", "CREATE TABLE dbo.Orders (Id INT NOT NULL PRIMARY KEY);\nGO\n");
        Write("migrations/V1__broken.sql", "INSERT INTO dbo.NoSuchTable (Id) VALUES (1);\n");

        await using var client = await ConnectAsync();

        // The gate requires a reviewed hash, so the agent diffs first — the same
        // path a real agent takes.
        var plan = Parse(await client.CallToolAsync("schemorph_diff",
            new Dictionary<string, object?> { ["schemaDir"] = SchemaDir }));
        var planHash = plan.GetProperty("planHash").GetString()!;

        var error = Parse(await client.CallToolAsync("schemorph_apply", new Dictionary<string, object?>
        {
            ["schemaDir"] = SchemaDir,
            ["expectedPlanHash"] = planHash,
            ["migrationsDir"] = MigrationsDir,
        })).GetProperty("error");

        Assert.Equal("migration_execution_failed", error.GetProperty("code").GetString());
        Assert.Equal("execution", error.GetProperty("kind").GetString());
        Assert.Equal("migration", error.GetProperty("stage").GetString());

        // The declarative stage committed before the migration ran. An agent that
        // could not see this would have to guess whether to re-plan or re-run.
        Assert.Equal(1, error.GetProperty("committed").GetProperty("declarative").GetInt32());
        Assert.Equal(0, error.GetProperty("committed").GetProperty("migrations").GetInt32());
    }

    [SkippableFact]
    public async Task An_unusable_temp_workspace_is_reported_as_itself_over_mcp()
    {
        // The MCP server takes its environment from the server entry, so this is
        // the shape a misconfigured server entry produces. It must name the
        // directory and the variable — never the internal .dacpac.
        Skip.IfNot(OperatingSystem.IsWindows(), "Uses an unmapped Windows drive letter to make temp unusable.");

        await using var client = await ConnectAsync(tempOverride: @"Z:\no\such\temp");

        var error = Parse(await client.CallToolAsync("schemorph_inspect",
                new Dictionary<string, object?> { ["outDir"] = Path.Combine(_dir, "out") }))
            .GetProperty("error");

        Assert.Equal("temp_workspace_unavailable", error.GetProperty("code").GetString());
        Assert.Equal("usage", error.GetProperty("kind").GetString());
        Assert.DoesNotContain(".dacpac", error.GetProperty("message").GetString()!);
        Assert.Contains("TMP", error.GetProperty("hint").GetString()!);
    }

    [SkippableFact]
    public async Task A_stale_hash_is_refused_without_a_stage_field()
    {
        // The absent-not-null rule, checked on the surface an agent parses: a
        // failure that never reached the database carries no stage, so an agent
        // branching on its presence is not misled.
        Write("schema/tables/dbo.Orders.sql", "CREATE TABLE dbo.Orders (Id INT NOT NULL PRIMARY KEY);\nGO\n");

        await using var client = await ConnectAsync();

        var error = Parse(await client.CallToolAsync("schemorph_apply", new Dictionary<string, object?>
        {
            ["schemaDir"] = SchemaDir,
            ["expectedPlanHash"] = "0000000000000000000000000000000000000000000000000000000000000000",
        })).GetProperty("error");

        Assert.Equal("plan_mismatch", error.GetProperty("code").GetString());
        Assert.False(error.TryGetProperty("stage", out _));
        Assert.False(error.TryGetProperty("committed", out _));
    }

    [SkippableFact]
    public async Task Every_tool_answers_a_failure_with_the_envelope()
    {
        // The gap this closes: error mapping had been added tool by tool, so two
        // of the four had none at all and their failures came back in the MCP
        // framework's shape instead. "One envelope everywhere" has to be checked
        // across the whole surface, not per tool as each is written.
        await using var client = await ConnectAsync();
        var missing = Path.Combine(_dir, "does-not-exist");

        var calls = new (string Tool, Dictionary<string, object?> Args)[]
        {
            ("schemorph_diff", new() { ["schemaDir"] = missing }),
            ("schemorph_status", new() { ["schemaDir"] = missing }),
            ("schemorph_inspect", new() { ["outDir"] = Path.Combine(_dir, "out2") }),
            ("schemorph_apply", new() { ["schemaDir"] = missing, ["expectedPlanHash"] = "deadbeef" }),
        };

        foreach (var (tool, args) in calls)
        {
            var response = Parse(await client.CallToolAsync(tool, args));

            // inspect is the one that can succeed here; the rest must fail. Either
            // way the response is Schemorph's own JSON, never the framework's.
            if (tool == "schemorph_inspect" && response.TryGetProperty("files", out _)) continue;

            var error = response.GetProperty("error");
            Assert.False(string.IsNullOrWhiteSpace(error.GetProperty("code").GetString()), tool);
            Assert.False(string.IsNullOrWhiteSpace(error.GetProperty("kind").GetString()), tool);
        }
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }
}
