using Npgsql;
using Schemorph.Core.Operations;

namespace Schemorph.Provider.Postgres.Tests;

/// <summary>
/// Final-review Critical regression: every other PG fixture's target schema
/// already exists before the first execution call — <see cref="PgTestSchema.CreateAsync"/>
/// pre-creates it — so nothing else here ever exercised
/// <see cref="PostgresProvider.BeginApplySessionAsync"/> against a schema that
/// genuinely does not exist yet. That structural blind spot is exactly what let
/// the self-deadlock through review: the session's own (uncommitted)
/// <c>CREATE SCHEMA IF NOT EXISTS</c> and <c>ledger.EnsureInitializedAsync</c>'s
/// SEPARATE-connection <c>CREATE SCHEMA IF NOT EXISTS</c> race on the same
/// never-created schema name — see the XML doc on
/// <see cref="PostgresProvider.BeginApplySessionAsync"/>.
/// </summary>
public sealed class FreshSchemaApplyTests : IAsyncDisposable
{
    private readonly string _schemaName = "schemorph_fresh_" + Guid.NewGuid().ToString("n")[..12];
    private readonly string _schemaDir = Path.Combine(
        Path.GetTempPath(), "schemorph-pg-fresh-" + Guid.NewGuid().ToString("n")[..8]);

    public async ValueTask DisposeAsync()
    {
        if (PgTestSchema.ServerUrl is not null)
        {
            await using var connection = new NpgsqlConnection(PgTestSchema.ServerUrl);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(
                $"DROP SCHEMA IF EXISTS \"{_schemaName}\" CASCADE", connection);
            await command.ExecuteNonQueryAsync();
        }
        try { Directory.Delete(_schemaDir, recursive: true); } catch { }
    }

    /// <summary>
    /// Bounded by a cancellation token well under Npgsql's default 30s
    /// CommandTimeout: against the unfixed code this test fails via cancellation
    /// in ~15s (the second connection's blocked CREATE SCHEMA is cancellable)
    /// rather than burning the full 30s every run. Against the fix, the apply
    /// completes in well under a second.
    /// </summary>
    [SkippableFact]
    public async Task First_apply_into_a_schema_that_does_not_exist_yet_completes_quickly()
    {
        Skip.If(PgTestSchema.ServerUrl is null, "SCHEMORPH_PG_TEST_URL is not set; Postgres tests need a live server.");

        Directory.CreateDirectory(Path.Combine(_schemaDir, "tables"));
        await File.WriteAllTextAsync(Path.Combine(_schemaDir, "tables", "Widgets.sql"), $"""
            CREATE TABLE "{_schemaName}"."Widgets" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                CONSTRAINT "PK_Widgets" PRIMARY KEY ("Id")
            );
            """);

        var url = new NpgsqlConnectionStringBuilder(PgTestSchema.ServerUrl!) { SearchPath = _schemaName }
            .ConnectionString;
        var provider = new PostgresProvider();
        var ledger = new PostgresLedgerStore();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var outcome = await ApplyOperation.RunAsync(
            provider, ledger, new ApplyOperation.Request(_schemaDir, url), cancellationToken: cts.Token);
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10),
            $"Apply into a not-yet-existing schema took {stopwatch.Elapsed} — expected well under 10s. " +
            "A near-15s failure here means the self-deadlock regressed.");
        Assert.True(outcome.Success, string.Join("; ", outcome.Errors.Select(e => e.Text)));
        Assert.Single(outcome.Applied);

        var live = await CatalogReader.ReadTablesAsync(PgTestSchema.ServerUrl!, _schemaName);
        Assert.Contains(live, t => t.Name == "Widgets");
    }
}
