using Npgsql;
using Schemorph.Core.Operations;
using Schemorph.Core.Planning;

namespace Schemorph.Provider.Postgres.Tests;

/// <summary>
/// P3 through the CORE orchestration (mirrors <see cref="PgCoreLoopTests"/>
/// for strategy 1): a view redefined idempotently, converging to an empty
/// re-diff, and re-defined again — without its own file changing — when a
/// table column it depends on changes shape.
/// </summary>
public class PgProgrammablesLoopTests : IAsyncLifetime
{
    private PgTestSchema _live = null!;
    private string _url = null!;
    private string _schemaDir = null!;
    private readonly PostgresProvider _provider = new();
    private readonly PostgresLedgerStore _ledger = new();

    private const string LiveV1 = """
        CREATE TABLE "Workspaces" (
            "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
            "Name" text NOT NULL,
            "Tier" integer NOT NULL,
            CONSTRAINT "PK_Workspaces" PRIMARY KEY ("Id")
        );
        """;

    public async Task InitializeAsync()
    {
        _live = await PgTestSchema.CreateAsync(LiveV1);
        _url = new NpgsqlConnectionStringBuilder(PgTestSchema.ServerUrl!) { SearchPath = _live.Name }
            .ConnectionString;

        _schemaDir = Path.Combine(Path.GetTempPath(), "schemorph-pg-prog-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(Path.Combine(_schemaDir, "tables"));
        Directory.CreateDirectory(Path.Combine(_schemaDir, "views"));
        // Programmable files carry bare, unqualified names (P3's single-schema
        // convention) — the redefine path sets search_path itself before running.
        await WriteWorkspacesTable("integer");
        await File.WriteAllTextAsync(Path.Combine(_schemaDir, "views", "ActiveWorkspaces.sql"), """
            CREATE VIEW "ActiveWorkspaces" AS SELECT "Id", "Name" FROM "Workspaces" WHERE "Tier" > 0;
            """);
    }

    public async Task DisposeAsync()
    {
        await _live.DisposeAsync();
        try { Directory.Delete(_schemaDir, recursive: true); } catch { }
    }

    private Task WriteWorkspacesTable(string tierType) => File.WriteAllTextAsync(
        Path.Combine(_schemaDir, "tables", "Workspaces.sql"), $"""
        CREATE TABLE "{_live.Name}"."Workspaces" (
            "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
            "Name" text NOT NULL,
            "Tier" {tierType} NOT NULL,
            CONSTRAINT "PK_Workspaces" PRIMARY KEY ("Id")
        );
        """);

    [SkippableFact]
    public async Task A_view_is_created_idempotently_and_converges()
    {
        var diff = await DiffOperation.RunAsync(_provider, _ledger, _schemaDir, _url, allowDestructive: false);
        Assert.True(diff.Success, string.Join("; ", diff.Errors.Select(e => e.Text)));
        var redefine = Assert.Single(diff.Plan!.Actions, a => a.Operation == PlanOperation.Redefine);
        Assert.Equal("ActiveWorkspaces", redefine.ObjectName);
        Assert.Contains("CREATE OR REPLACE VIEW", redefine.Sql, StringComparison.OrdinalIgnoreCase);

        var expected = PlanFingerprint.Compute(diff.Plan!);
        var outcome = await ApplyOperation.RunAsync(_provider, _ledger,
            new ApplyOperation.Request(_schemaDir, _url, ExpectedPlanHash: expected));
        Assert.True(outcome.Success, string.Join("; ", outcome.Errors.Select(e => e.Text)));
        Assert.Contains("ActiveWorkspaces", outcome.Redefines!.Redefined);

        await using (var connection = new NpgsqlConnection(_url))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand("SELECT count(*) FROM \"ActiveWorkspaces\"", connection);
            Assert.Equal(0L, await command.ExecuteScalarAsync());
        }

        var rediff = await DiffOperation.RunAsync(_provider, _ledger, _schemaDir, _url, allowDestructive: false);
        Assert.True(rediff.Success);
        Assert.False(rediff.Plan!.HasChanges);
    }

    [SkippableFact]
    public async Task A_column_type_change_redefines_the_dependent_view_though_its_file_did_not_change()
    {
        // First apply, at the baseline — establishes ledger history for the view.
        var baseline = await DiffOperation.RunAsync(_provider, _ledger, _schemaDir, _url, allowDestructive: false);
        var baselineHash = PlanFingerprint.Compute(baseline.Plan!);
        await ApplyOperation.RunAsync(_provider, _ledger,
            new ApplyOperation.Request(_schemaDir, _url, ExpectedPlanHash: baselineHash));

        // Retype the column the view selects — the view's own file is untouched.
        await WriteWorkspacesTable("bigint");

        var diff = await DiffOperation.RunAsync(_provider, _ledger, _schemaDir, _url, allowDestructive: false);
        Assert.True(diff.Success, string.Join("; ", diff.Errors.Select(e => e.Text)));
        Assert.Contains(diff.Plan!.Actions,
            a => a.Operation == PlanOperation.Redefine && a.ObjectName == "ActiveWorkspaces");
    }
}
