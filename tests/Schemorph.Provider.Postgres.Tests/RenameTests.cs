using Npgsql;
using Schemorph.Core.Operations;
using Schemorph.Core.Planning;

namespace Schemorph.Provider.Postgres.Tests;

/// <summary>
/// What a rename becomes when the only signal is a name.
///
/// Desired state and live state are matched by name, so an object that changed
/// its name is not one object seen twice — it is one that stopped being declared
/// and another that started. The plan says so: a drop and a create, never a
/// rename. That is not a gap in the matcher but the limit of the input; two
/// snapshots taken a release apart carry nothing that distinguishes a rename from
/// a removal plus an unrelated addition, and inferring one from shape would be a
/// guess the tool then executes.
///
/// The point of pinning it is that the failure is *quiet* in the only place a
/// reviewer looks: the row survives, the table survives, and the column with the
/// expected name is right there — holding nothing. So the evidence here is
/// values, never plans.
/// </summary>
public class RenameTests : IAsyncLifetime
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
        _schemaDir = Path.Combine(Path.GetTempPath(), "schemorph-pg-rename-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(Path.Combine(_schemaDir, "tables"));
    }

    public async Task DisposeAsync()
    {
        await _live.DisposeAsync();
        try { Directory.Delete(_schemaDir, recursive: true); } catch { }
    }

    /// <summary>
    /// The same table with one column renamed — nothing else differs, so no other
    /// difference can explain the verdict.
    /// </summary>
    private Task WriteNotesRenamedToRemarks() => File.WriteAllTextAsync(
        Path.Combine(_schemaDir, "tables", "Workspaces.sql"), $"""
        CREATE TABLE "{_live.Name}"."Workspaces" (
            "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
            "Name" text NOT NULL,
            "Remarks" text,
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

    private async Task ExecuteCore(string sql)
    {
        await using var connection = new NpgsqlConnection(_url);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private Task<long> Count(string from, string where) =>
        Scalar<long>($"SELECT count(*) FROM {from} WHERE {where}");

    private Task<long> Columns(string name) => Count(
        "information_schema.columns",
        $"table_schema = '{_live.Name}' AND table_name = 'Workspaces' AND column_name = '{name}'");

    [SkippableFact]
    public async Task A_renamed_column_reaches_the_plan_as_a_drop_beside_an_add()
    {
        await WriteNotesRenamedToRemarks();

        var diff = await DiffOperation.RunAsync(_provider, _ledger, _schemaDir, _url, allowDestructive: false);
        Assert.True(diff.Success, string.Join("; ", diff.Errors.Select(e => e.Text)));

        // Both halves are in the engine's script, and they are independent
        // statements — the tool never had a rename to emit.
        Assert.Contains("ADD COLUMN", diff.UpdateScript!);
        Assert.Contains("DROP COLUMN", diff.UpdateScript!);
        Assert.DoesNotContain("RENAME", diff.UpdateScript!, StringComparison.OrdinalIgnoreCase);

        // Which means the destructive gate sees it, and holds the whole table back.
        Assert.Empty(diff.Plan!.Actions);
        Assert.Contains(diff.Plan.Messages, m => m.Code == "SCHEMORPH001");
        Assert.Contains(diff.Plan.Excluded, e => e.ObjectName == "Workspaces");

        var outcome = await ApplyOperation.RunAsync(_provider, _ledger,
            new ApplyOperation.Request(_schemaDir, _url));
        Assert.True(outcome.Success, string.Join("; ", outcome.Errors.Select(e => e.Text)));
        Assert.Empty(outcome.Applied);

        Assert.Equal(1, await Columns("Notes"));
        Assert.Equal(0, await Columns("Remarks"));
        Assert.Equal("keep me", await Scalar<string>("SELECT \"Notes\" FROM \"Workspaces\" LIMIT 1"));
    }

    /// <summary>
    /// Forced through, the shape arrives and the values do not. This is the whole
    /// hazard in one assertion: the row is still there and the column has the name
    /// the files asked for, so every check short of reading the value passes.
    /// </summary>
    [SkippableFact]
    public async Task Allowing_the_destructive_change_moves_the_name_but_not_the_values()
    {
        await WriteNotesRenamedToRemarks();

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
        Assert.Equal(1, await Columns("Remarks"));

        // The row survived the apply. Its value did not follow the name.
        Assert.Equal(1L, await Scalar<long>("SELECT count(*) FROM \"Workspaces\""));
        Assert.Equal(1L, await Scalar<long>("SELECT count(*) FROM \"Workspaces\" WHERE \"Remarks\" IS NULL"));
    }

    /// <summary>
    /// The way out, and the reason the limitation is livable: rename with the
    /// engine's own statement, which carries the identity the files cannot, and
    /// the next diff has nothing to say.
    /// </summary>
    [SkippableFact]
    public async Task Renaming_with_the_engine_reconciles_and_keeps_the_values()
    {
        await WriteNotesRenamedToRemarks();

        await ExecuteCore($"ALTER TABLE \"{_live.Name}\".\"Workspaces\" RENAME COLUMN \"Notes\" TO \"Remarks\";");

        var diff = await DiffOperation.RunAsync(_provider, _ledger, _schemaDir, _url, allowDestructive: false);
        Assert.True(diff.Success, string.Join("; ", diff.Errors.Select(e => e.Text)));

        // Converged: the desired state was already true before the tool looked.
        Assert.Empty(diff.Plan!.Actions);
        Assert.DoesNotContain(diff.Plan.Messages, m => m.Code == "SCHEMORPH001");

        Assert.Equal("keep me", await Scalar<string>("SELECT \"Remarks\" FROM \"Workspaces\" LIMIT 1"));
    }

    /// <summary>
    /// At table granularity the same limit holds, and the per-object gate makes it
    /// look stranger: the create is safe on its own and applies, the drop is
    /// withheld, and what is left is an empty table beside the full one.
    /// </summary>
    [SkippableFact]
    public async Task A_renamed_table_becomes_a_create_beside_a_withheld_drop()
    {
        await File.WriteAllTextAsync(Path.Combine(_schemaDir, "tables", "Workareas.sql"), $"""
            CREATE TABLE "{_live.Name}"."Workareas" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "Name" text NOT NULL,
                "Notes" text,
                CONSTRAINT "PK_Workareas" PRIMARY KEY ("Id")
            );
            """);

        var diff = await DiffOperation.RunAsync(_provider, _ledger, _schemaDir, _url, allowDestructive: false);
        Assert.True(diff.Success, string.Join("; ", diff.Errors.Select(e => e.Text)));

        // The drop is gated; the create is not, and nothing ties them together.
        Assert.Contains(diff.Plan!.Excluded, e => e.ObjectName == "Workspaces");
        Assert.Contains(diff.Plan.Actions, a => a.ObjectName == "Workareas");

        var outcome = await ApplyOperation.RunAsync(_provider, _ledger,
            new ApplyOperation.Request(_schemaDir, _url,
                ExpectedPlanHash: PlanFingerprint.Compute(diff.Plan)));
        Assert.True(outcome.Success, string.Join("; ", outcome.Errors.Select(e => e.Text)));

        // Both tables now exist, and the rows stayed with the old name.
        Assert.Equal(1L, await Count("information_schema.tables",
            $"table_schema = '{_live.Name}' AND table_name = 'Workspaces'"));
        Assert.Equal(1L, await Count("information_schema.tables",
            $"table_schema = '{_live.Name}' AND table_name = 'Workareas'"));
        Assert.Equal(1L, await Scalar<long>("SELECT count(*) FROM \"Workspaces\""));
        Assert.Equal(0L, await Scalar<long>("SELECT count(*) FROM \"Workareas\""));
    }
}
