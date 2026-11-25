using Npgsql;
using Schemorph.Core.Operations;

namespace Schemorph.Provider.Postgres.Tests;

/// <summary>
/// A partial index's predicate has the same parse instability as a CHECK expression (see
/// <see cref="CheckNormalizationConvergenceTests"/>): a varchar IN-list is stored as
/// <c>= ANY ((ARRAY[…])::text[])</c> on the first parse and with per-element casts on the second, and the live side always
/// takes the second. Without the same fixed-point pass on the shadow, every partial index whose predicate is a varchar
/// IN-list re-plans as a drop and create on every run.
/// </summary>
public class PartialIndexNormalizationConvergenceTests : IAsyncLifetime
{
    private PostgresTestSchema _live = null!;
    private string _url = null!;
    private string _schemaDir = null!;
    private readonly PostgresProvider _provider = new();
    private readonly PostgresLedgerStore _ledger = new();

    public async Task InitializeAsync()
    {
        _live = await PostgresTestSchema.CreateAsync("SELECT 1;");
        _url = new NpgsqlConnectionStringBuilder(PostgresTestSchema.ServerUrl!) { SearchPath = _live.Name }
            .ConnectionString;

        _schemaDir = Path.Combine(
            Path.GetTempPath(), "schemorph-pg-ix-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(Path.Combine(_schemaDir, "tables"));
        await File.WriteAllTextAsync(Path.Combine(_schemaDir, "tables", "work_request.sql"), $"""
            CREATE TABLE "{_live.Name}".work_request (
                id uuid NOT NULL,
                status varchar(20) NOT NULL DEFAULT 'draft',
                CONSTRAINT pk_work_request PRIMARY KEY (id)
            );

            CREATE UNIQUE INDEX ux_work_request_open ON "{_live.Name}".work_request (status) WHERE status IN ('draft', 'submitted');
            """);
    }

    public async Task DisposeAsync()
    {
        await _live.DisposeAsync();
        try { Directory.Delete(_schemaDir, recursive: true); } catch { }
    }

    [SkippableFact]
    public async Task Varchar_in_list_index_predicate_converges_after_apply()
    {
        var apply = await ApplyOperation.RunAsync(_provider, _ledger,
            new ApplyOperation.Request(_schemaDir, _url, ExpectedPlanHash: null));
        Assert.True(apply.Success, string.Join("; ", apply.Errors.Select(e => e.Text)));

        // The convergence contract: what apply wrote is what diff reads back.
        var rediff = await DiffOperation.RunAsync(_provider, _ledger, _schemaDir, _url, allowDestructive: false);
        Assert.True(rediff.Success, string.Join("; ", rediff.Errors.Select(e => e.Text)));
        Assert.False(rediff.Plan!.HasChanges,
            "a varchar IN-list index predicate must not re-plan as a perpetual drop and create after apply");

        // Stability: a second apply is a no-op and the loop stays empty.
        var applyAgain = await ApplyOperation.RunAsync(_provider, _ledger,
            new ApplyOperation.Request(_schemaDir, _url, ExpectedPlanHash: null));
        Assert.True(applyAgain.Success);
        Assert.Empty(applyAgain.Applied);

        var rediffAgain = await DiffOperation.RunAsync(_provider, _ledger, _schemaDir, _url, allowDestructive: false);
        Assert.False(rediffAgain.Plan!.HasChanges);
    }
}
