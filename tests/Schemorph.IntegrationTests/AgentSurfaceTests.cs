using System.Text.Json;
using ModelContextProtocol.Client;

namespace Schemorph.IntegrationTests;

/// <summary>
/// The agent-usability harness (ROADMAP Phase 2): the exit-criterion scenario —
/// edit SQL → diff → review the plan → gated apply — driven the way an agent
/// drives it, over the REAL MCP stdio surface (the CLI binary as a child
/// process, spoken to with the official MCP client), parsing only machine
/// contracts (plan format, error envelope). No human-oriented output is read.
/// </summary>
public sealed class AgentSurfaceTests : IDisposable
{
    private readonly TestDatabase _db = new();
    private readonly string _dir = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), $"schemorph-agent-{Guid.NewGuid():N}")).FullName;

    private string SchemaDir => Path.Combine(_dir, "schema");

    private void Write(string relativePath, string content)
    {
        var path = Path.Combine(_dir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    /// <summary>The CLI assembly rides along via the project reference; `dotnet exec` runs it unbuffered.</summary>
    private static string CliDll => Path.Combine(AppContext.BaseDirectory, "schemorph.dll");

    private async Task<McpClient> ConnectAsync(string? schemaDirEnv = null)
    {
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "schemorph",
            Command = "dotnet",
            Arguments = new[] { CliDll, "mcp" },
            EnvironmentVariables = new Dictionary<string, string?>
            {
                ["SCHEMORPH_URL"] = _db.Url,
                ["SCHEMORPH_SCHEMA_DIR"] = schemaDirEnv,
            },
        });
        return await McpClient.CreateAsync(transport);
    }

    private static JsonElement Parse(ModelContextProtocol.Protocol.CallToolResult result)
    {
        var text = Assert.IsType<ModelContextProtocol.Protocol.TextContentBlock>(Assert.Single(result.Content)).Text;
        return JsonDocument.Parse(text).RootElement;
    }

    [SkippableFact]
    public async Task Agent_completes_a_schema_change_end_to_end_over_mcp()
    {
        // A database with an initial state the "team" already deployed.
        _db.Execute("CREATE TABLE dbo.Customers (Id INT NOT NULL PRIMARY KEY, Name NVARCHAR(100) NOT NULL)");
        _db.Execute("CREATE VIEW dbo.CustomerNames AS SELECT Id, Name FROM dbo.Customers");
        Write("schema/tables/dbo.Customers.sql",
            "CREATE TABLE dbo.Customers (Id INT NOT NULL PRIMARY KEY, Name NVARCHAR(100) NOT NULL);\nGO\n");
        Write("schema/views/dbo.CustomerNames.sql",
            "CREATE VIEW dbo.CustomerNames AS SELECT Id, Name FROM dbo.Customers\nGO\n");

        await using var client = await ConnectAsync();

        // Discovery: the four tools are there; mutating apply is not marked read-only.
        var tools = await client.ListToolsAsync();
        Assert.Equal(
            new[] { "schemorph_apply", "schemorph_diff", "schemorph_inspect", "schemorph_status" },
            tools.Select(t => t.Name).OrderBy(n => n).ToArray());

        // 1. The agent edits the SQL (a column and a view change).
        Write("schema/tables/dbo.Customers.sql",
            "CREATE TABLE dbo.Customers (Id INT NOT NULL PRIMARY KEY, Name NVARCHAR(100) NOT NULL, Email NVARCHAR(200) NULL);\nGO\n");
        Write("schema/views/dbo.CustomerNames.sql",
            "CREATE VIEW dbo.CustomerNames AS SELECT Id, Name, Email FROM dbo.Customers\nGO\n");

        // 2. diff — the reviewable plan, as the versioned machine contract.
        var plan = Parse(await client.CallToolAsync("schemorph_diff",
            new Dictionary<string, object?> { ["schemaDir"] = SchemaDir }));
        Assert.True(plan.GetProperty("hasChanges").GetBoolean());
        var planHash = plan.GetProperty("planHash").GetString()!;
        var changed = plan.GetProperty("changes").EnumerateArray()
            .Select(c => (Name: c.GetProperty("objectName").GetString(), Action: c.GetProperty("actions")[0].GetString()))
            .ToList();
        Assert.Contains(("dbo.Customers", "alter"), changed!);
        Assert.Contains(("dbo.CustomerNames", "redefine"), changed!);

        // Plan explanations (format 1.2): the alter carries its attributed SQL
        // slice, the redefine its exact idempotent script; both explain themselves.
        var byName = plan.GetProperty("changes").EnumerateArray()
            .ToDictionary(c => c.GetProperty("objectName").GetString()!);
        Assert.Contains("ALTER TABLE", byName["dbo.Customers"].GetProperty("sql").GetString());
        Assert.Contains("CREATE OR ALTER", byName["dbo.CustomerNames"].GetProperty("sql").GetString());
        Assert.All(byName.Values, c =>
            Assert.False(string.IsNullOrWhiteSpace(c.GetProperty("explanation").GetString())));

        // 3. A stale fingerprint is refused, and nothing has executed.
        var refused = Parse(await client.CallToolAsync("schemorph_apply",
            new Dictionary<string, object?> { ["schemaDir"] = SchemaDir, ["expectedPlanHash"] = new string('0', 64) }));
        Assert.Equal("plan_mismatch", refused.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("invalid_state", refused.GetProperty("error").GetProperty("kind").GetString());
        Assert.Equal(0, _db.Scalar<int>(
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME='Customers' AND COLUMN_NAME='Email'"));

        // 4. The reviewed fingerprint applies.
        var applied = Parse(await client.CallToolAsync("schemorph_apply",
            new Dictionary<string, object?> { ["schemaDir"] = SchemaDir, ["expectedPlanHash"] = planHash }));
        Assert.Contains("dbo.Customers", applied.GetProperty("applied").EnumerateArray()
            .Select(c => c.GetProperty("objectName").GetString()));
        Assert.Contains("dbo.CustomerNames", applied.GetProperty("redefines").GetProperty("applied")
            .EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(1, _db.Scalar<int>(
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME='Customers' AND COLUMN_NAME='Email'"));

        // 5. Convergence: status reports nothing left to do.
        var status = Parse(await client.CallToolAsync("schemorph_status",
            new Dictionary<string, object?> { ["schemaDir"] = SchemaDir }));
        Assert.False(status.GetProperty("hasPendingWork").GetBoolean());
        Assert.False(status.GetProperty("plan").GetProperty("hasChanges").GetBoolean());
    }

    [SkippableFact]
    public async Task Schema_and_plan_are_readable_as_mcp_resources()
    {
        // Deployed state + a desired-state edit waiting to be applied.
        _db.Execute("CREATE TABLE dbo.Customers (Id INT NOT NULL PRIMARY KEY, Name NVARCHAR(100) NOT NULL)");
        Write("schema/tables/dbo.Customers.sql",
            "CREATE TABLE dbo.Customers (Id INT NOT NULL PRIMARY KEY, Name NVARCHAR(100) NOT NULL, Email NVARCHAR(200) NULL);\nGO\n");

        await using var client = await ConnectAsync(schemaDirEnv: SchemaDir);

        // Discovery: fixed resources and the per-object template are announced.
        var resources = await client.ListResourcesAsync();
        Assert.Equal(new[] { "schemorph://plan", "schemorph://schema" },
            resources.Select(r => r.Uri).OrderBy(u => u).ToArray());
        var templates = await client.ListResourceTemplatesAsync();
        Assert.Contains("schemorph://schema/{kind}/{name}", templates.Select(t => t.UriTemplate));

        // schemorph://schema — the live database as desired-state SQL (no Email yet).
        var schema = Assert.IsType<ModelContextProtocol.Protocol.TextResourceContents>(
            Assert.Single((await client.ReadResourceAsync("schemorph://schema")).Contents));
        Assert.Contains("-- tables/dbo.Customers.sql", schema.Text);
        Assert.Contains("CREATE TABLE", schema.Text);
        Assert.DoesNotContain("Email", schema.Text);

        // schemorph://schema/{kind}/{name} — one object.
        var table = Assert.IsType<ModelContextProtocol.Protocol.TextResourceContents>(
            Assert.Single((await client.ReadResourceAsync("schemorph://schema/tables/dbo.Customers")).Contents));
        Assert.Contains("CREATE TABLE", table.Text);

        // schemorph://plan — the same plan (and planHash) schemorph_diff computes.
        var planText = Assert.IsType<ModelContextProtocol.Protocol.TextResourceContents>(
            Assert.Single((await client.ReadResourceAsync("schemorph://plan")).Contents));
        var plan = JsonDocument.Parse(planText.Text).RootElement;
        Assert.True(plan.GetProperty("hasChanges").GetBoolean());
        var diff = Parse(await client.CallToolAsync("schemorph_diff",
            new Dictionary<string, object?> { ["schemaDir"] = SchemaDir }));
        Assert.Equal(diff.GetProperty("planHash").GetString(), plan.GetProperty("planHash").GetString());
    }

    [SkippableFact]
    public async Task Plan_resource_without_schema_dir_is_a_usage_error_not_a_crash()
    {
        await using var client = await ConnectAsync();

        // The server's McpException surfaces to the client as a protocol error.
        var error = await Assert.ThrowsAsync<ModelContextProtocol.McpProtocolException>(
            async () => await client.ReadResourceAsync("schemorph://plan"));

        Assert.Contains("SCHEMORPH_SCHEMA_DIR", error.Message);
    }

    [SkippableFact]
    public async Task Missing_connection_string_is_a_usage_error_not_a_crash()
    {
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "schemorph",
            Command = "dotnet",
            Arguments = new[] { CliDll, "mcp" },
            EnvironmentVariables = new Dictionary<string, string?> { ["SCHEMORPH_URL"] = null },
        });
        await using var client = await McpClient.CreateAsync(transport);
        Directory.CreateDirectory(SchemaDir);

        var error = Parse(await client.CallToolAsync("schemorph_diff",
            new Dictionary<string, object?> { ["schemaDir"] = SchemaDir }));

        Assert.Equal("usage", error.GetProperty("error").GetProperty("kind").GetString());
        Assert.Contains("SCHEMORPH_URL", error.GetProperty("error").GetProperty("message").GetString());
    }

    public void Dispose()
    {
        _db.Dispose();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }
}
