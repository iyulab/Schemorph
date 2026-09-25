using Npgsql;
using Schemorph.Core.Operations;
using Schemorph.Core.Planning;

namespace Schemorph.Provider.Postgres.Tests;

/// <summary>
/// The destructive gate at column granularity, against a live engine and rows
/// that either survive or do not.
///
/// A plan is built per object, so a column the desired state stops declaring
/// arrives as an ALTER of its table: the same shape as adding a default or
/// widening a type, and classified from that shape alone it was an ordinary
/// warning. The gate therefore stood only in front of whole-table drops, and the
/// far more common way to lose a column's rows went through it by default —
/// visible in the review script, silent in the plan, the risk level and the lint
/// band alike.
///
/// Read rows rather than plans wherever the claim is about data: what a plan says
/// about loss is the thing under test, so it cannot also be the evidence.
/// </summary>
public class ColumnDropGateTests : IAsyncLifetime
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
            "Notes" text,
            CONSTRAINT "PK_Workspaces" PRIMARY KEY ("Id")
        );
        CREATE INDEX "IX_Workspaces_Name" ON "Workspaces" ("Name");
        INSERT INTO "Workspaces" ("Name", "Notes") VALUES ('alpha', 'keep me');
        """;

    public async Task InitializeAsync()
    {
        _live = await PgTestSchema.CreateAsync(LiveV1);
        _url = new NpgsqlConnectionStringBuilder(PgTestSchema.ServerUrl!) { SearchPath = _live.Name }
            .ConnectionString;
        _schemaDir = Path.Combine(Path.GetTempPath(), "schemorph-pg-drop-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(Path.Combine(_schemaDir, "tables"));
    }

    public async Task DisposeAsync()
    {
        await _live.DisposeAsync();
        try { Directory.Delete(_schemaDir, recursive: true); } catch { }
    }

    /// <summary>
    /// The desired state, minus <c>Notes</c> and still declaring the index — so
    /// the only difference is the column, and nothing else can explain a verdict.
    /// </summary>
    private Task WriteDesiredWithoutNotes() => File.WriteAllTextAsync(
        Path.Combine(_schemaDir, "tables", "Workspaces.sql"), $"""
        CREATE TABLE "{_live.Name}"."Workspaces" (
            "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
            "Name" text NOT NULL,
            CONSTRAINT "PK_Workspaces" PRIMARY KEY ("Id")
        );
        CREATE INDEX "IX_Workspaces_Name" ON "{_live.Name}"."Workspaces" ("Name");
        """);

    private async Task<T> Scalar<T>(string sql)
    {
        await using var connection = new NpgsqlConnection(_url);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    private Task<long> Count(string from, string where) =>
        Scalar<long>($"SELECT count(*) FROM {from} WHERE {where}");

    private Task<long> Columns(string name) => Count(
        "information_schema.columns",
        $"table_schema = '{_live.Name}' AND table_name = 'Workspaces' AND column_name = '{name}'");

    [SkippableFact]
    public async Task A_column_the_files_stop_declaring_is_refused_without_the_gate()
    {
        await WriteDesiredWithoutNotes();

        var diff = await DiffOperation.RunAsync(_provider, _ledger, _schemaDir, _url, allowDestructive: false);
        Assert.True(diff.Success, string.Join("; ", diff.Errors.Select(e => e.Text)));

        // Nothing to execute, and the plan says why rather than coming back empty
        // and unexplained — the reviewer is being told a decision was made for them.
        Assert.Empty(diff.Plan!.Actions);
        Assert.Contains(diff.Plan.Messages, m => m.Code == "SCHEMORPH001");
        Assert.Contains(diff.Plan.Excluded, e => e.ObjectName == "Workspaces");

        // The engine's script still carries the statement, which is exactly why the
        // exclusion list has to name it (plan format 1.6).
        Assert.Contains("DROP COLUMN", diff.UpdateScript!);

        var outcome = await ApplyOperation.RunAsync(_provider, _ledger,
            new ApplyOperation.Request(_schemaDir, _url));
        Assert.True(outcome.Success, string.Join("; ", outcome.Errors.Select(e => e.Text)));
        Assert.Empty(outcome.Applied);

        // The claim is about data, so the evidence is data.
        Assert.Equal(1, await Columns("Notes"));
        Assert.Equal("keep me", await Scalar<string>("SELECT \"Notes\" FROM \"Workspaces\" LIMIT 1"));
    }

    [SkippableFact]
    public async Task The_same_change_applies_once_destructive_changes_are_allowed()
    {
        await WriteDesiredWithoutNotes();

        var diff = await DiffOperation.RunAsync(_provider, _ledger, _schemaDir, _url, allowDestructive: true);
        Assert.True(diff.Success, string.Join("; ", diff.Errors.Select(e => e.Text)));

        var action = Assert.Single(diff.Plan!.Actions);
        Assert.Equal(RiskLevel.Destructive, action.Risk);
        Assert.True(diff.Plan.HasDestructiveChanges);
        Assert.Contains(diff.Plan.Messages, m => m.Code == "SCHEMORPH103");

        var outcome = await ApplyOperation.RunAsync(_provider, _ledger,
            new ApplyOperation.Request(_schemaDir, _url, AllowDestructive: true,
                ExpectedPlanHash: PlanFingerprint.Compute(diff.Plan)));
        Assert.True(outcome.Success, string.Join("; ", outcome.Errors.Select(e => e.Text)));

        Assert.Equal(0, await Columns("Notes"));
        Assert.Equal(1L, await Scalar<long>("SELECT count(*) FROM \"Workspaces\""));   // the surviving row

        // Convergence still holds through the destructive path: the gate changes
        // what a plan may contain, never whether applying it settles.
        var rediff = await DiffOperation.RunAsync(_provider, _ledger, _schemaDir, _url, allowDestructive: true);
        Assert.True(rediff.Success, string.Join("; ", rediff.Errors.Select(e => e.Text)));
        Assert.Empty(rediff.Plan!.Actions);
    }

    /// <summary>
    /// A column being added and one being removed reach the plan as one entry on
    /// one table. Only the removal loses anything, so only the removal is gated: the
    /// addition runs beside it. Held back together — as they once were, because the
    /// gate could only answer for the whole object — the desired state's safe half
    /// stayed unapplied while a re-diff reported nothing applicable, and code built
    /// against the new column met a table without it.
    /// </summary>
    [SkippableFact]
    public async Task A_safe_change_sharing_the_table_runs_while_the_column_drop_waits()
    {
        await File.WriteAllTextAsync(Path.Combine(_schemaDir, "tables", "Workspaces.sql"), $"""
            CREATE TABLE "{_live.Name}"."Workspaces" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "Name" text NOT NULL,
                "Tier" text NOT NULL DEFAULT 'free',
                CONSTRAINT "PK_Workspaces" PRIMARY KEY ("Id")
            );
            CREATE INDEX "IX_Workspaces_Name" ON "{_live.Name}"."Workspaces" ("Name");
            """);

        var gated = await DiffOperation.RunAsync(_provider, _ledger, _schemaDir, _url, allowDestructive: false);
        Assert.True(gated.Success, string.Join("; ", gated.Errors.Select(e => e.Text)));

        var action = Assert.Single(gated.Plan!.Actions);
        Assert.Equal(RiskLevel.Warning, action.Risk);
        Assert.Contains("ADD COLUMN", action.Sql!);
        Assert.DoesNotContain("DROP COLUMN", action.Sql!);
        Assert.False(gated.Plan.HasDestructiveChanges);
        var exclusion = Assert.Single(gated.Plan.Excluded);
        Assert.Contains("DROP COLUMN \"Notes\"", exclusion.Statements!);
        Assert.Contains(gated.Plan.Messages, m => m.Code == "SCHEMORPH001" && m.ObjectName == "Workspaces");

        // The apply must run exactly the reviewed remainder: the plan it recomputes from
        // its own masked synthesis has to hash the same, or it refuses.
        var outcome = await ApplyOperation.RunAsync(_provider, _ledger,
            new ApplyOperation.Request(_schemaDir, _url,
                ExpectedPlanHash: PlanFingerprint.Compute(gated.Plan)));
        Assert.True(outcome.Success, string.Join("; ", outcome.Errors.Select(e => e.Text)));

        // The claim is about data: the addition landed, the removed column and its rows did not go.
        Assert.Equal(1, await Columns("Tier"));
        Assert.Equal(1, await Columns("Notes"));
        Assert.Equal("keep me", await Scalar<string>("SELECT \"Notes\" FROM \"Workspaces\" LIMIT 1"));

        // Everything but the drop has settled: a re-diff holds no action, only the refusal.
        var rediff = await DiffOperation.RunAsync(_provider, _ledger, _schemaDir, _url, allowDestructive: false);
        Assert.True(rediff.Success, string.Join("; ", rediff.Errors.Select(e => e.Text)));
        Assert.Empty(rediff.Plan!.Actions);
        Assert.Contains(rediff.Plan.Messages, m => m.Code == "SCHEMORPH001");

        // And the drop, once enabled, completes it.
        var allowed = await DiffOperation.RunAsync(_provider, _ledger, _schemaDir, _url, allowDestructive: true);
        var drop = await ApplyOperation.RunAsync(_provider, _ledger,
            new ApplyOperation.Request(_schemaDir, _url, AllowDestructive: true,
                ExpectedPlanHash: PlanFingerprint.Compute(allowed.Plan!)));
        Assert.True(drop.Success, string.Join("; ", drop.Errors.Select(e => e.Text)));
        Assert.Equal(0, await Columns("Notes"));
    }

    /// <summary>
    /// An index the files do not declare is dropped and no data is lost, so it is
    /// deliberately not gated — measured on both engines at 0.7.0. What changes
    /// here is that the band stops being silent about it: query cost is the thing
    /// at stake, and a reviewer who has learned that this band speaks up would
    /// otherwise read its silence as "nothing to lose".
    /// </summary>
    [SkippableFact]
    public async Task An_undeclared_index_is_dropped_ungated_but_no_longer_silently()
    {
        await File.WriteAllTextAsync(Path.Combine(_schemaDir, "tables", "Workspaces.sql"), $"""
            CREATE TABLE "{_live.Name}"."Workspaces" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "Name" text NOT NULL,
                "Notes" text,
                CONSTRAINT "PK_Workspaces" PRIMARY KEY ("Id")
            );
            """);

        var diff = await DiffOperation.RunAsync(_provider, _ledger, _schemaDir, _url, allowDestructive: false);
        Assert.True(diff.Success, string.Join("; ", diff.Errors.Select(e => e.Text)));

        var action = Assert.Single(diff.Plan!.Actions);
        Assert.Equal(RiskLevel.Warning, action.Risk);          // not gated, and not pretending to be
        Assert.Contains(diff.Plan.Messages, m => m.Code == "SCHEMORPH108");
        Assert.DoesNotContain(diff.Plan.Messages, m => m.Code == "SCHEMORPH001");

        var outcome = await ApplyOperation.RunAsync(_provider, _ledger,
            new ApplyOperation.Request(_schemaDir, _url,
                ExpectedPlanHash: PlanFingerprint.Compute(diff.Plan)));
        Assert.True(outcome.Success, string.Join("; ", outcome.Errors.Select(e => e.Text)));

        Assert.Equal(0, await Count("pg_indexes",
            $"schemaname = '{_live.Name}' AND indexname = 'IX_Workspaces_Name'"));
    }
}
