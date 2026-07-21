using Schemorph.Core.Ledger;
using Schemorph.Core.Migrations;
using Schemorph.Core.Providers;

namespace Schemorph.Core.Tests.Migrations;

public sealed class MigrationRunnerTests : IDisposable
{
    private readonly string _dir = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), $"schemorph-mig-{Guid.NewGuid():N}")).FullName;
    private readonly FakeLedger _ledger = new();
    private readonly FakeProvider _provider;

    public MigrationRunnerTests() => _provider = new FakeProvider { Ledger = _ledger };

    private MigrationRunner Runner => new(_provider, _ledger);

    private string WriteMigration(string fileName, string content)
    {
        var path = Path.Combine(_dir, fileName);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public async Task Runs_pending_migrations_in_numeric_version_order()
    {
        // Lexicographic order would run V10 before V2 — numeric order must win.
        WriteMigration("V10__second.sql", "INSERT B;");
        WriteMigration("V2__first.sql", "INSERT A;");

        var result = await Runner.RunAsync(_dir, "conn");

        Assert.Equal(new[] { "V2__first.sql", "V10__second.sql" }, result.Applied);
        Assert.Equal(new[] { "INSERT A;", "INSERT B;" }, _provider.ExecutedScripts);
        Assert.Equal(2, _ledger.Entries.Count);
        Assert.All(_ledger.Entries, e => Assert.Equal(MigrationRunner.LedgerKind, e.Kind));
    }

    [Fact]
    public async Task Pending_migrations_carry_lint_warnings_applied_ones_stay_silent()
    {
        var applied = WriteMigration("V1__old.sql", "TRUNCATE TABLE dbo.Log;");
        WriteMigration("V2__risky.sql", "TRUNCATE TABLE dbo.Log;");
        _ledger.Entries.Add(new LedgerEntry(MigrationRunner.LedgerKind, "V1__old.sql", "Run",
            MigrationScript.ComputeChecksum(File.ReadAllText(applied)), true, null));
        _provider.LintSignals.Add(MigrationLintSignal.Truncate);

        var plan = await Runner.PlanAsync(_dir, "conn");

        // Only the pending script warns — history is history.
        var warning = Assert.Single(plan.Warnings);
        Assert.Equal("SCHEMORPH104", warning.Code);
        Assert.Contains("V2__risky.sql", warning.Text);
        Assert.Equal("Warning", warning.Severity);
    }

    [Fact]
    public async Task Already_applied_migrations_are_skipped()
    {
        var path = WriteMigration("V1__seed.sql", "INSERT A;");
        _ledger.Entries.Add(new LedgerEntry(MigrationRunner.LedgerKind, "V1__seed.sql", "Run",
            MigrationScript.ComputeChecksum(File.ReadAllText(path)), true, null));

        var result = await Runner.RunAsync(_dir, "conn");

        Assert.Empty(result.Applied);
        Assert.Equal(1, result.Skipped);
        Assert.Empty(_provider.ExecutedScripts);
    }

    [Fact]
    public async Task Tampered_applied_migration_fails_before_anything_runs()
    {
        WriteMigration("V1__seed.sql", "INSERT A -- tampered;");
        WriteMigration("V2__next.sql", "INSERT B;");
        _ledger.Entries.Add(new LedgerEntry(MigrationRunner.LedgerKind, "V1__seed.sql", "Run",
            "0000deadbeef", true, null));

        var ex = await Assert.ThrowsAsync<MigrationException>(() => Runner.RunAsync(_dir, "conn"));

        Assert.Contains("V1__seed.sql", ex.Message);
        Assert.Empty(_provider.ExecutedScripts);   // fail-fast: pending V2 must not run
    }

    [Fact]
    public async Task Duplicate_versions_are_rejected()
    {
        WriteMigration("V1__a.sql", "A;");
        WriteMigration("V01__b.sql", "B;");

        await Assert.ThrowsAsync<MigrationException>(() => Runner.RunAsync(_dir, "conn"));
    }

    [Fact]
    public async Task Checksum_is_stable_across_line_endings()
    {
        Assert.Equal(
            MigrationScript.ComputeChecksum("INSERT A;\r\nINSERT B;"),
            MigrationScript.ComputeChecksum("INSERT A;\nINSERT B;"));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Migration_commits_its_ledger_entry_with_the_script()
    {
        // ADR-0004 decision 2: script and ledger row are one atomic unit — the entry
        // must travel WITH the script into the provider, not in a later append.
        var path = WriteMigration("V1__seed.sql", "INSERT A;");

        await Runner.RunAsync(_dir, "conn");

        var (script, entries) = Assert.Single(_provider.AtomicExecutions);
        Assert.Equal("INSERT A;", script);
        var entry = Assert.Single(entries);
        Assert.Equal("V1__seed.sql", entry.ObjectName);
        Assert.Equal(MigrationScript.ComputeChecksum(File.ReadAllText(path)), entry.Checksum);
        Assert.True(entry.Succeeded);
    }

    [Fact]
    public async Task Failed_migration_records_a_failure_row_and_stops()
    {
        WriteMigration("V1__ok.sql", "INSERT A;");
        WriteMigration("V2__bad.sql", "FAILING;");
        WriteMigration("V3__after.sql", "INSERT C;");
        var provider = new FakeProvider { Ledger = _ledger, FailOnScriptContaining = "FAILING" };

        var ex = await Assert.ThrowsAsync<MigrationExecutionException>(
            () => new MigrationRunner(provider, _ledger).RunAsync(_dir, "conn"));

        // Run-once makes "which ones already ran" the first question asked, and
        // the answer exists only here.
        Assert.Equal("V2__bad.sql", ex.FileName);
        Assert.Equal(new[] { "V1__ok.sql" }, ex.Applied);
        Assert.Equal(new[] { "INSERT A;" }, provider.ExecutedScripts);   // fail-fast: V3 never ran
        var failure = Assert.Single(_ledger.Entries, e => !e.Succeeded);
        Assert.Equal("V2__bad.sql", failure.ObjectName);
        Assert.Contains("boom", failure.Detail);
    }

    [Fact]
    public async Task Failure_row_write_failure_never_masks_the_original_error()
    {
        WriteMigration("V1__bad.sql", "FAILING;");
        var provider = new FakeProvider { Ledger = _ledger, FailOnScriptContaining = "FAILING" };
        _ledger.AppendThrows = true;

        var ex = await Assert.ThrowsAsync<MigrationExecutionException>(
            () => new MigrationRunner(provider, _ledger).RunAsync(_dir, "conn"));

        Assert.Contains("boom", ex.Message);   // the script error, not "ledger unavailable"
        Assert.Contains("boom", ex.InnerException!.Message);   // and reachable unwrapped
    }

    [Fact]
    public async Task Non_matching_file_names_are_ignored_but_reported()
    {
        WriteMigration("V1__ok.sql", "A;");
        WriteMigration("notes.sql", "not a migration");

        var result = await Runner.RunAsync(_dir, "conn");

        Assert.Equal(new[] { "V1__ok.sql" }, result.Applied);
        Assert.Equal(new[] { "notes.sql" }, result.IgnoredFiles);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);
}
