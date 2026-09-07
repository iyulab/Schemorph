using Npgsql;
using Schemorph.Core.Operations;
using Schemorph.Core.Planning;

namespace Schemorph.Provider.Postgres.Tests;

/// <summary>
/// A column removed and not put back, on this engine. The rename tests reach the
/// criterion already, but always through a drop that arrives beside an add — and
/// a desired-state edit that simply deletes a column line is the plainer way to
/// reach it, so the claim that the criterion is measured on both engines should
/// rest on the plain shape too.
///
/// The second assertion is the one worth the file. <c>hasDestructiveChanges</c>
/// reports what the plan still holds, and gating works by taking the change out —
/// so a correctly refused plan reports <c>false</c>. That reading is documented on
/// the plan-format page, which is provider-neutral, so it has to be true here and
/// not only where it was first observed.
/// </summary>
public class ColumnRemovalTests : IAsyncLifetime
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
        INSERT INTO "Workspaces" ("Name", "Notes") VALUES ('alpha', 'keep me');
        """;

    public async Task InitializeAsync()
    {
        _live = await PgTestSchema.CreateAsync(LiveV1);
        _url = new NpgsqlConnectionStringBuilder(PgTestSchema.ServerUrl!) { SearchPath = _live.Name }
            .ConnectionString;
        _schemaDir = Path.Combine(Path.GetTempPath(), "schemorph-pg-removal-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(Path.Combine(_schemaDir, "tables"));
    }

    public async Task DisposeAsync()
    {
        await _live.DisposeAsync();
        try { Directory.Delete(_schemaDir, recursive: true); } catch { }
    }

    /// <summary>The same table with one column simply gone — nothing else differs.</summary>
    private Task WriteWithoutNotes() => File.WriteAllTextAsync(
        Path.Combine(_schemaDir, "tables", "Workspaces.sql"), $"""
        CREATE TABLE "{_live.Name}"."Workspaces" (
            "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
            "Name" text NOT NULL,
            CONSTRAINT "PK_Workspaces" PRIMARY KEY ("Id")
        );
        """);

    private async Task<T> Scalar<T>(string sql)
    {
        await using var connection = new NpgsqlConnection(_url);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    private Task<long> Columns(string name) => Scalar<long>(
        "SELECT count(*) FROM information_schema.columns " +
        $"WHERE table_schema = '{_live.Name}' AND table_name = 'Workspaces' AND column_name = '{name}'");

    [SkippableFact]
    public async Task Removing_a_column_outright_is_withheld_without_the_flag()
    {
        await WriteWithoutNotes();

        var diff = await DiffOperation.RunAsync(_provider, _ledger, _schemaDir, _url, allowDestructive: false);
        Assert.True(diff.Success, string.Join("; ", diff.Errors.Select(e => e.Text)));

        Assert.Contains("DROP COLUMN", diff.UpdateScript!);
        Assert.Empty(diff.Plan!.Actions);
        Assert.Contains(diff.Plan.Messages, m => m.Code == "SCHEMORPH001");
        Assert.Contains(diff.Plan.Excluded, e => e.ObjectName == "Workspaces");

        // The refusal is the signal; the flag is not. Same reading as the other
        // engine, because the page that documents it does not name an engine.
        Assert.False(diff.Plan.HasDestructiveChanges);

        var outcome = await ApplyOperation.RunAsync(_provider, _ledger,
            new ApplyOperation.Request(_schemaDir, _url));
        Assert.True(outcome.Success, string.Join("; ", outcome.Errors.Select(e => e.Text)));
        Assert.Empty(outcome.Applied);

        Assert.Equal(1, await Columns("Notes"));
        Assert.Equal("keep me", await Scalar<string>("SELECT \"Notes\" FROM \"Workspaces\" LIMIT 1"));
    }

    [SkippableFact]
    public async Task Allowing_the_removal_takes_the_column_and_its_values()
    {
        await WriteWithoutNotes();

        var diff = await DiffOperation.RunAsync(_provider, _ledger, _schemaDir, _url, allowDestructive: true);
        Assert.True(diff.Success, string.Join("; ", diff.Errors.Select(e => e.Text)));

        var action = Assert.Single(diff.Plan!.Actions);
        Assert.Equal(RiskLevel.Destructive, action.Risk);
        Assert.True(diff.Plan.HasDestructiveChanges);

        var outcome = await ApplyOperation.RunAsync(_provider, _ledger,
            new ApplyOperation.Request(_schemaDir, _url, AllowDestructive: true,
                ExpectedPlanHash: PlanFingerprint.Compute(diff.Plan)));
        Assert.True(outcome.Success, string.Join("; ", outcome.Errors.Select(e => e.Text)));

        Assert.Equal(0, await Columns("Notes"));
        Assert.Equal(1L, await Scalar<long>("SELECT count(*) FROM \"Workspaces\""));
    }
}
