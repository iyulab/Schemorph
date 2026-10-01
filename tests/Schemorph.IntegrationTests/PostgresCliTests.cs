using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Schemorph.IntegrationTests;

/// <summary>
/// The provider-selection surface, end to end over the real binary: with
/// <c>SCHEMORPH_PROVIDER=postgres</c> the same verbs, plan format and exit
/// codes run against Postgres — the contract layer of parity (ADR-0003).
/// Gated on SCHEMORPH_PG_TEST_URL like every live Postgres test.
/// </summary>
public sealed class PostgresCliTests : IDisposable
{
    private static string? ServerUrl => Environment.GetEnvironmentVariable("SCHEMORPH_PG_TEST_URL");

    private static string CliDll => Path.Combine(AppContext.BaseDirectory, "schemorph.dll");

    private readonly string _dir = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), $"schemorph-pgcli-{Guid.NewGuid():N}")).FullName;

    // A unique schema per test run; the target server is a throwaway container
    // (local schemorph-pg-p0 or the CI service), so no teardown is owed.
    private readonly string _schema = "schemorph_cli_" + Guid.NewGuid().ToString("n")[..8];

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private sealed record CliResult(int ExitCode, string StdOut, string StdErr);

    private static CliResult Run(string arguments, string url)
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
        psi.Environment["SCHEMORPH_PROVIDER"] = "postgres";

        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return new CliResult(process.ExitCode, stdout, stderr);
    }

    private string Url()
        => ServerUrl!.TrimEnd(';') + $";Search Path={_schema}";

    [SkippableFact]
    public void The_manifest_reflects_the_selected_provider()
    {
        Skip.If(ServerUrl is null, "SCHEMORPH_PG_TEST_URL is not set; Postgres CLI tests need a live server.");

        var manifest = Run("schema", Url());
        Assert.Equal(0, manifest.ExitCode);

        var provider = JsonDocument.Parse(manifest.StdOut).RootElement.GetProperty("provider");
        Assert.Equal("postgres", provider.GetProperty("name").GetString());
        Assert.Equal("transactional", provider.GetProperty("atomicity").GetString());
    }

    [SkippableFact]
    public void Diff_apply_rediff_run_identically_to_the_sqlserver_loop()
    {
        Skip.If(ServerUrl is null, "SCHEMORPH_PG_TEST_URL is not set; Postgres CLI tests need a live server.");

        Directory.CreateDirectory(Path.Combine(_dir, "tables"));
        File.WriteAllText(Path.Combine(_dir, "tables", "Notes.sql"), $"""
            CREATE TABLE "{_schema}"."Notes" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "Body" text NOT NULL,
                CONSTRAINT "PK_Notes" PRIMARY KEY ("Id")
            );
            """);

        // diff: pending changes, exit 2, plan format 1.3 with the earned guarantee.
        var pending = Run($"diff --schema \"{_dir}\"", Url());
        Assert.Equal(2, pending.ExitCode);
        var plan = JsonDocument.Parse(pending.StdOut).RootElement;
        Assert.Equal("transactional", plan.GetProperty("atomicity").GetString());
        var planHash = plan.GetProperty("planHash").GetString()!;

        // gated apply, then convergence — the same machine contract as SQL Server.
        var applied = Run($"apply --schema \"{_dir}\" --expect-plan {planHash}", Url());
        Assert.Equal(0, applied.ExitCode);

        var converged = Run($"diff --schema \"{_dir}\"", Url());
        Assert.Equal(0, converged.ExitCode);
    }

    [SkippableFact]
    public void An_engine_error_during_diff_carries_its_code_and_translation()
    {
        Skip.If(ServerUrl is null, "SCHEMORPH_PG_TEST_URL is not set; Postgres CLI tests need a live server.");

        Directory.CreateDirectory(Path.Combine(_dir, "tables"));
        Directory.CreateDirectory(Path.Combine(_dir, "views"));
        var table = Path.Combine(_dir, "tables", "Notes.sql");
        File.WriteAllText(table,
            $"""CREATE TABLE "{_schema}"."Notes" ("Id" integer NOT NULL PRIMARY KEY, "Body" text NOT NULL);""");
        File.WriteAllText(Path.Combine(_dir, "views", "NoteBodies.sql"),
            """CREATE VIEW "NoteBodies" AS SELECT "Body" FROM "Notes";""");
        Assert.Equal(0, Run($"apply --schema \"{_dir}\"", Url()).ExitCode);

        // The table stops declaring the column its view still reads: the desired state
        // contradicts itself, and the engine says so when the comparison probes the view.
        // That is the file's fault, so it is reported as one — naming the view and its file —
        // and the engine's code still rides in the envelope's machine-readable slot.
        File.WriteAllText(table, $"""CREATE TABLE "{_schema}"."Notes" ("Id" integer NOT NULL PRIMARY KEY);""");
        var result = Run($"diff --schema \"{_dir}\" --format json", Url());

        Assert.Equal(1, result.ExitCode);
        var error = JsonDocument.Parse(result.StdErr).RootElement.GetProperty("error");
        Assert.Equal("invalid_desired_state", error.GetProperty("code").GetString());
        var engine = error.GetProperty("engine");
        Assert.Equal("42703", engine.GetProperty("code").GetString());
        Assert.True(engine.GetProperty("translated").GetBoolean());
        var message = error.GetProperty("message").GetString();
        Assert.StartsWith("SCHEMORPH013: NoteBodies (", message);
        Assert.Contains("NoteBodies.sql", message);
        Assert.Contains("42703: column \"Body\" does not exist", message);
        Assert.Contains("References a column that does not exist", message);
    }
}
