using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Client;

namespace Schemorph.IntegrationTests;

/// <summary>
/// The lock-timeout option as a user meets it, over the real binary — the command line and the MCP
/// tool — for the two ways it is turned down before any database is touched: an argument that is not
/// a timeout, and a provider whose apply is not one transaction. (What a timeout does to a statement
/// on a live PostgreSQL server is covered by that provider's own tests.) Nothing here needs a
/// database: the connection string is never opened, which is the point being asserted.
/// </summary>
public sealed class LockTimeoutSurfaceTests : IDisposable
{
    private readonly string _dir = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), $"schemorph-locktimeout-{Guid.NewGuid():N}")).FullName;

    private const string NeverOpened = "Server=unreachable.invalid;Database=never;User Id=u;Password=p;Connect Timeout=1";

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static string CliDll => Path.Combine(AppContext.BaseDirectory, "schemorph.dll");

    private (int ExitCode, JsonElement Error) RunCli(string arguments)
    {
        var psi = new ProcessStartInfo("dotnet", $"exec \"{CliDll}\" {arguments}")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.Environment["SCHEMORPH_URL"] = NeverOpened;
        psi.Environment.Remove("SCHEMORPH_PROVIDER");   // the default provider: SQL Server

        using var process = Process.Start(psi)!;
        // stderr is drained concurrently: a child that fills its pipe (usage text on an unknown verb) would block while we wait on stdout alone.
        var stderrTask = process.StandardError.ReadToEndAsync();
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = stderrTask.GetAwaiter().GetResult();
        process.WaitForExit();
        // Redirected output is json: the envelope is on stderr for a failure.
        var envelope = JsonDocument.Parse(string.IsNullOrWhiteSpace(stderr) ? stdout : stderr).RootElement;
        return (process.ExitCode, envelope.GetProperty("error"));
    }

    [Theory]
    [InlineData("30")]      // no unit
    [InlineData("0s")]      // zero means "no timeout" — leave the option out
    [InlineData("soon")]
    public void The_cli_refuses_a_value_that_is_not_a_lock_timeout(string value)
    {
        var (exit, error) = RunCli($"apply --schema \"{_dir}\" --lock-timeout {value}");

        Assert.Equal(1, exit);
        Assert.Equal("invalid_arguments", error.GetProperty("code").GetString());
        Assert.Contains("lock timeout", error.GetProperty("message").GetString());
    }

    [Fact]
    public void The_cli_refuses_a_lock_timeout_on_a_provider_whose_apply_is_not_one_transaction()
    {
        var (exit, error) = RunCli($"apply --schema \"{_dir}\" --lock-timeout 5s");

        Assert.Equal(1, exit);
        Assert.Equal("not_implemented", error.GetProperty("code").GetString());
        Assert.Equal("unsupported", error.GetProperty("kind").GetString());
        Assert.Contains("lock timeout", error.GetProperty("message").GetString());
    }

    [Fact]
    public void The_manifest_lists_the_option()
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

        var apply = manifest.GetProperty("verbs").EnumerateArray()
            .Single(c => c.GetProperty("name").GetString() == "apply");
        var flags = apply.GetProperty("options").EnumerateArray().Select(o => o.GetProperty("flag").GetString());
        Assert.Contains("--lock-timeout", flags);
    }

    private async Task<McpClient> ConnectAsync()
    {
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "schemorph",
            Command = "dotnet",
            Arguments = new[] { CliDll, "mcp" },
            EnvironmentVariables = new Dictionary<string, string?> { ["SCHEMORPH_URL"] = NeverOpened },
        });
        return await McpClient.CreateAsync(transport);
    }

    private static JsonElement ErrorOf(ModelContextProtocol.Protocol.CallToolResult result)
    {
        Assert.True(result.IsError);
        var text = Assert.IsType<ModelContextProtocol.Protocol.TextContentBlock>(Assert.Single(result.Content)).Text;
        return JsonDocument.Parse(text).RootElement.GetProperty("error");
    }

    [Theory]
    [InlineData("30", "invalid_arguments")]
    [InlineData("5s", "not_implemented")]   // valid, but the default provider cannot honor it
    public async Task The_mcp_apply_tool_turns_the_option_down_the_same_way(string lockTimeout, string expectedCode)
    {
        await using var client = await ConnectAsync();

        var result = await client.CallToolAsync("schemorph_apply", new Dictionary<string, object?>
        {
            ["schemaDir"] = _dir,
            ["expectedPlanHash"] = new string('0', 64),
            ["lockTimeout"] = lockTimeout,
        });

        Assert.Equal(expectedCode, ErrorOf(result).GetProperty("code").GetString());
    }

    [Fact]
    public async Task The_mcp_apply_tool_advertises_the_option_as_optional()
    {
        await using var client = await ConnectAsync();

        var apply = (await client.ListToolsAsync()).Single(t => t.Name == "schemorph_apply");
        var schema = apply.JsonSchema;

        Assert.True(schema.GetProperty("properties").TryGetProperty("lockTimeout", out _));
        var required = schema.TryGetProperty("required", out var r)
            ? r.EnumerateArray().Select(x => x.GetString()).ToList()
            : new List<string?>();
        Assert.DoesNotContain("lockTimeout", required);
    }
}
