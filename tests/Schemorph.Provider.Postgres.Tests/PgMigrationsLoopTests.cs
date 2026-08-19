using Npgsql;
using Schemorph.Core.Operations;
using Schemorph.Core.Providers;

namespace Schemorph.Provider.Postgres.Tests;

/// <summary>
/// P4 (strategy 3, ADR-0002) through the CORE orchestration (mirrors
/// <see cref="PgCoreLoopTests"/> for strategy 1 and
/// <see cref="PgProgrammablesLoopTests"/> for strategy 2): a versioned
/// migration runs once, is recorded in the ledger, and is skipped — not
/// re-run — on a second apply.
/// </summary>
public class PgMigrationsLoopTests : IAsyncLifetime
{
    private PgTestSchema _live = null!;
    private string _url = null!;
    private string _schemaDir = null!;
    private string _migrationsDir = null!;
    private readonly PostgresProvider _provider = new();
    private readonly PostgresLedgerStore _ledger = new();

    private const string LiveV1 = """
        CREATE TABLE "Counters" (
            "Name" text NOT NULL,
            "Value" integer NOT NULL,
            CONSTRAINT "PK_Counters" PRIMARY KEY ("Name")
        );
        """;

    public async Task InitializeAsync()
    {
        _live = await PgTestSchema.CreateAsync(LiveV1);
        _url = new NpgsqlConnectionStringBuilder(PgTestSchema.ServerUrl!) { SearchPath = _live.Name }
            .ConnectionString;

        _schemaDir = Path.Combine(Path.GetTempPath(), "schemorph-pg-mig-schema-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(Path.Combine(_schemaDir, "tables"));
        await File.WriteAllTextAsync(Path.Combine(_schemaDir, "tables", "Counters.sql"), $"""
            CREATE TABLE "{_live.Name}"."Counters" (
                "Name" text NOT NULL,
                "Value" integer NOT NULL,
                CONSTRAINT "PK_Counters" PRIMARY KEY ("Name")
            );
            """);

        _migrationsDir = Path.Combine(Path.GetTempPath(), "schemorph-pg-mig-data-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(_migrationsDir);
        // Seed data migrations are unqualified/bare, like programmable files —
        // the redefine/migration execution path sets search_path itself.
        await File.WriteAllTextAsync(Path.Combine(_migrationsDir, "V0001__seed.sql"), """
            INSERT INTO "Counters" ("Name", "Value") VALUES ('visits', 0);
            """);
    }

    public async Task DisposeAsync()
    {
        await _live.DisposeAsync();
        try { Directory.Delete(_schemaDir, recursive: true); } catch { }
        try { Directory.Delete(_migrationsDir, recursive: true); } catch { }
    }

    [SkippableFact]
    public async Task A_migration_runs_once_and_is_skipped_on_the_next_apply()
    {
        var request = new ApplyOperation.Request(_schemaDir, _url, MigrationsDir: _migrationsDir);

        var first = await ApplyOperation.RunAsync(_provider, _ledger, request);
        Assert.True(first.Success, string.Join("; ", first.Errors.Select(e => e.Text)));
        Assert.Contains("V0001__seed.sql", first.Migrations!.Applied);

        await using (var connection = new NpgsqlConnection(_url))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(
                "SELECT \"Value\" FROM \"Counters\" WHERE \"Name\" = 'visits'", connection);
            Assert.Equal(0, await command.ExecuteScalarAsync());
        }

        var second = await ApplyOperation.RunAsync(_provider, _ledger, request);
        Assert.True(second.Success, string.Join("; ", second.Errors.Select(e => e.Text)));
        Assert.Empty(second.Migrations!.Applied);
        Assert.Equal(1, second.Migrations!.Skipped);
    }

    [SkippableFact]
    public async Task An_unfiltered_delete_is_linted_as_a_plan_warning()
    {
        await File.WriteAllTextAsync(Path.Combine(_migrationsDir, "V0002__purge.sql"), """
            DELETE FROM "Counters";
            """);

        var diff = await DiffOperation.RunAsync(_provider, _ledger, _schemaDir, _url, allowDestructive: false);
        Assert.True(diff.Success);

        var plan = await new Schemorph.Core.Migrations.MigrationRunner(_provider, _ledger)
            .PlanAsync(_migrationsDir, _url);
        Assert.Contains(plan.Warnings, w => w.Code == "SCHEMORPH105");
    }
}
