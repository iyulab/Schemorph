using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Schemorph.IntegrationTests;

/// <summary>
/// A PostgreSQL user who has not set <c>SCHEMORPH_PROVIDER</c> gets the default SQL Server provider, which reads the
/// PostgreSQL files first and refuses them before it ever opens the connection. That error says nothing about the
/// connection string, so the hint naming the provider variable has to come from the string itself. No database is
/// needed: nothing here gets as far as connecting.
/// </summary>
public sealed class ProviderHintCliTests : IDisposable
{
    private readonly string _dir = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), $"schemorph-hint-{Guid.NewGuid():N}")).FullName;

    private static string CliDll => Path.Combine(AppContext.BaseDirectory, "schemorph.dll");

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private (int ExitCode, string StdErr) Diff(string url, string? urlEnvironment)
    {
        var tables = Directory.CreateDirectory(Path.Combine(_dir, "schema", "tables")).FullName;
        File.WriteAllText(Path.Combine(tables, "author.sql"),
            "CREATE TABLE public.author (id uuid NOT NULL DEFAULT gen_random_uuid(), name varchar(80) NOT NULL, " +
            "CONSTRAINT pk_author PRIMARY KEY (id));\n");

        var arguments = $"diff --schema \"{Path.Combine(_dir, "schema")}\"" + (urlEnvironment is null ? $" --url \"{url}\"" : "");
        var psi = new ProcessStartInfo("dotnet", $"exec \"{CliDll}\" {arguments}")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        // Pinned both ways: an ambient variable on the machine running the tests must not choose the provider or the URL.
        psi.Environment.Remove("SCHEMORPH_PROVIDER");
        psi.Environment.Remove("SCHEMORPH_URL");
        if (urlEnvironment is not null) psi.Environment["SCHEMORPH_URL"] = urlEnvironment;

        using var process = Process.Start(psi)!;
        var stderr = process.StandardError.ReadToEnd();
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, stderr);
    }

    [Fact]
    public void PostgreSQL_files_under_the_default_provider_name_the_provider_variable_when_the_url_is_a_PostgreSQL_string()
    {
        var (exitCode, stderr) = Diff("Host=localhost;Username=u;Password=p;Database=d", urlEnvironment: null);

        Assert.NotEqual(0, exitCode);
        var error = JsonDocument.Parse(stderr).RootElement.GetProperty("error");
        Assert.Contains("SCHEMORPH_PROVIDER=postgres", error.GetProperty("hint").GetString());
    }

    [Fact]
    public void The_same_holds_when_the_url_comes_from_the_environment()
    {
        var (exitCode, stderr) = Diff("", urlEnvironment: "Host=localhost;Username=u;Password=p;Database=d");

        Assert.NotEqual(0, exitCode);
        var error = JsonDocument.Parse(stderr).RootElement.GetProperty("error");
        Assert.Contains("SCHEMORPH_PROVIDER=postgres", error.GetProperty("hint").GetString());
    }

    [Fact]
    public void A_valid_SQL_Server_string_gets_no_provider_hint_for_the_same_files()
    {
        var (exitCode, stderr) = Diff("Server=unused;Database=unused;Encrypt=False", urlEnvironment: null);

        Assert.NotEqual(0, exitCode);
        var error = JsonDocument.Parse(stderr).RootElement.GetProperty("error");
        // The files are still refused, but nothing is wrong with the connection string, so nothing blames it.
        var hint = error.TryGetProperty("hint", out var h) ? h.GetString() : null;
        Assert.DoesNotContain("SCHEMORPH_PROVIDER", hint ?? "");
    }
}
