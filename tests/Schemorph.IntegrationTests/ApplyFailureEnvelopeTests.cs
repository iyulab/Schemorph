using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Schemorph.IntegrationTests;

/// <summary>
/// What a failed <c>apply</c> tells the operator, verified through the real
/// binary. Apply runs its strategies in order without rolling back across them
/// (ADR-0004), so a failure in a later stage leaves the earlier ones committed.
/// The first production consumer met exactly this, got a generic error, and
/// recorded the wrong state in a runbook — so the envelope must name the stage
/// and what it left behind, and no hint may point at something the tool never
/// checked (docs/failure-semantics.md, docs/errors.md).
///
/// The migration stage is what these drive: a migration script is executed as
/// written, so an invalid one fails deterministically after the declarative
/// publish has committed — no engine-version-dependent setup required.
/// </summary>
public sealed class ApplyFailureEnvelopeTests : IDisposable
{
    private readonly TestDatabase _db = new();
    private readonly string _dir = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), $"schemorph-envelope-{Guid.NewGuid():N}")).FullName;

    private string SchemaDir => Path.Combine(_dir, "schema");
    private string MigrationsDir => Path.Combine(_dir, "migrations");

    private static string CliDll => Path.Combine(AppContext.BaseDirectory, "schemorph.dll");

    private sealed record CliResult(int ExitCode, string StdOut, string StdErr);

    private static CliResult Run(string arguments)
    {
        var psi = new ProcessStartInfo("dotnet", $"exec \"{CliDll}\" {arguments}")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return new CliResult(process.ExitCode, stdout, stderr);
    }

    /// <summary>One table to create declaratively, and one migration that cannot run.</summary>
    private void SeedFailingMigration()
    {
        Directory.CreateDirectory(Path.Combine(SchemaDir, "tables"));
        File.WriteAllText(Path.Combine(SchemaDir, "tables", "dbo.Orders.sql"),
            "CREATE TABLE dbo.Orders (Id INT NOT NULL PRIMARY KEY);");

        Directory.CreateDirectory(MigrationsDir);
        File.WriteAllText(Path.Combine(MigrationsDir, "V1__broken.sql"),
            "INSERT INTO dbo.NoSuchTable (Id) VALUES (1);");
    }

    [SkippableFact]
    public void A_migration_failure_names_its_stage_and_what_had_committed()
    {
        SeedFailingMigration();

        var result = Run($"apply --url \"{_db.Url}\" --schema \"{SchemaDir}\" " +
                         $"--migrations \"{MigrationsDir}\" --format json");

        Assert.Equal(1, result.ExitCode);
        var error = JsonDocument.Parse(result.StdErr).RootElement.GetProperty("error");

        Assert.Equal("migration_execution_failed", error.GetProperty("code").GetString());
        Assert.Equal("execution", error.GetProperty("kind").GetString());
        Assert.Equal("migration", error.GetProperty("stage").GetString());

        // The declarative stage committed before the migration ran. Reporting
        // nothing here is what sent a consumer's runbook wrong.
        var committed = error.GetProperty("committed");
        Assert.Equal(1, committed.GetProperty("declarative").GetInt32());
        Assert.Equal(0, committed.GetProperty("migrations").GetInt32());

        // Re-running is the resume path; the hint must say so rather than guess.
        var hint = error.GetProperty("hint").GetString()!;
        Assert.Contains("failure-semantics", hint);
        Assert.DoesNotContain("connection string", hint);
    }

    [SkippableFact]
    public void The_table_really_was_created_before_the_migration_failed()
    {
        SeedFailingMigration();

        Run($"apply --url \"{_db.Url}\" --schema \"{SchemaDir}\" " +
            $"--migrations \"{MigrationsDir}\" --format json");

        // The envelope's claim is checked against the database itself — an
        // envelope that says "1 declarative change committed" is only worth
        // anything if that is true.
        Assert.Equal(1, _db.Scalar<int>("SELECT COUNT(*) FROM sys.tables WHERE name = 'Orders'"));
    }

    [SkippableFact]
    public void An_error_without_a_stage_keeps_the_shape_it_always_had()
    {
        // Optional fields are absent, not null: a consumer parsing the pre-existing
        // envelope must see no difference on errors that have no stage.
        var result = Run("apply --format json");

        Assert.Equal(1, result.ExitCode);
        var error = JsonDocument.Parse(result.StdErr).RootElement.GetProperty("error");
        Assert.Equal("invalid_arguments", error.GetProperty("code").GetString());
        Assert.False(error.TryGetProperty("stage", out _));
        Assert.False(error.TryGetProperty("committed", out _));
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }
}
