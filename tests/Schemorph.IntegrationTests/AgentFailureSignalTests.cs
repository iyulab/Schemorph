using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Schemorph.IntegrationTests;

/// <summary>
/// How a failed call is SIGNALLED to an agent over the real MCP stdio surface. <see cref="AgentErrorSurfaceTests"/>
/// pins the envelope an error carries (kind, code, hint, stage); this pins the two things around it that decide
/// whether a model ever reads it: a failure is marked <c>isError</c> (the MCP signal a client branches on, not a
/// success whose body happens to say "error"), and a call that leaves out a required argument gets the same envelope
/// naming the argument instead of the SDK's masked "An error occurred invoking …". None of these need a database —
/// every case fails before a connection is made — so they run everywhere.
/// </summary>
public sealed class AgentFailureSignalTests : IDisposable
{
    private readonly string _dir = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), $"schemorph-signal-{Guid.NewGuid():N}")).FullName;

    private static string CliDll => Path.Combine(AppContext.BaseDirectory, "schemorph.dll");

    private static async Task<McpClient> ConnectAsync(string url, string? provider = null)
    {
        var env = new Dictionary<string, string?>
        {
            ["SCHEMORPH_URL"] = url,
            // Pinned both ways: an ambient variable on the machine running the tests must not choose the provider.
            ["SCHEMORPH_PROVIDER"] = provider,
        };
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "schemorph",
            Command = "dotnet",
            Arguments = new[] { CliDll, "mcp" },
            EnvironmentVariables = env,
        });
        return await McpClient.CreateAsync(transport);
    }

    private static JsonElement Error(CallToolResult result)
    {
        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
        return JsonDocument.Parse(text).RootElement.GetProperty("error");
    }

    [Fact]
    public async Task A_structured_error_is_marked_isError_so_the_client_sees_a_failed_call()
    {
        await using var client = await ConnectAsync("Server=unused;Database=unused");

        var result = await client.CallToolAsync("schemorph_diff",
            new Dictionary<string, object?> { ["schemaDir"] = Path.Combine(_dir, "no-such-dir") });

        Assert.True(result.IsError);
        var error = Error(result);
        Assert.Equal("schema_dir_not_found", error.GetProperty("code").GetString());
        Assert.Equal("usage", error.GetProperty("kind").GetString());
        Assert.False(string.IsNullOrWhiteSpace(error.GetProperty("hint").GetString()));
    }

    [Fact]
    public async Task A_missing_required_argument_is_answered_in_the_envelope_naming_the_argument()
    {
        await using var client = await ConnectAsync("Server=unused;Database=unused");

        var result = await client.CallToolAsync("schemorph_diff", new Dictionary<string, object?>());

        Assert.True(result.IsError);
        var error = Error(result);
        Assert.Equal("invalid_arguments", error.GetProperty("code").GetString());
        Assert.Equal("usage", error.GetProperty("kind").GetString());
        Assert.Contains("schemaDir", error.GetProperty("message").GetString());
        Assert.DoesNotContain("An error occurred invoking", error.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Every_missing_required_argument_is_named_at_once()
    {
        await using var client = await ConnectAsync("Server=unused;Database=unused");

        var result = await client.CallToolAsync("schemorph_apply", new Dictionary<string, object?>());

        Assert.True(result.IsError);
        var message = Error(result).GetProperty("message").GetString();
        Assert.Contains("schemaDir", message);
        Assert.Contains("expectedPlanHash", message);
    }

    [Fact]
    public async Task A_postgres_connection_string_under_the_default_provider_names_the_provider_variable()
    {
        var schema = Directory.CreateDirectory(Path.Combine(_dir, "schema")).FullName;
        await using var client = await ConnectAsync("Host=unused;Username=unused;Database=unused");

        var result = await client.CallToolAsync("schemorph_diff",
            new Dictionary<string, object?> { ["schemaDir"] = schema });

        Assert.True(result.IsError);
        var error = Error(result);
        Assert.Equal("compare_failed", error.GetProperty("code").GetString());
        var hint = error.GetProperty("hint").GetString();
        Assert.Contains("SCHEMORPH_PROVIDER=postgres", hint);
        // A hint, not a correction: the selection stays the variable's.
        Assert.Contains("If it is a PostgreSQL connection string", hint);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }
}
