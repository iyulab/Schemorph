using Npgsql;
using Schemorph.Core.Operations;

namespace Schemorph.Provider.Postgres.Tests;

/// <summary>
/// CHECK expressions are the one P1 constraint class whose catalog rendering is
/// not parse-stable: a varchar IN-list parses to <c>(ARRAY[…]::text[])</c> on the
/// first round and to per-element <c>(…::character varying)::text</c> casts when
/// that rendering is parsed again. The live side always goes through the second
/// parse (apply executes synthesized DDL built from shadow renderings), so
/// without a fixed-point pass on the shadow the loop never converges — found by
/// the mdd-booster PG-dialect chain E2E (37-table consumer corpus, 2026-07-23),
/// where every enum CHECK table re-planned as an alter forever.
///
/// Text-typed CHECKs never exposed this (elements are already text, no relabel),
/// which is why the existing corpus tests stayed green.
/// </summary>
public class CheckNormalizationConvergenceTests : IAsyncLifetime
{
    private PgTestSchema _live = null!;
    private string _url = null!;
    private string _schemaDir = null!;
    private readonly PostgresProvider _provider = new();
    private readonly PostgresLedgerStore _ledger = new();

    public async Task InitializeAsync()
    {
        _live = await PgTestSchema.CreateAsync("SELECT 1;");
        _url = new NpgsqlConnectionStringBuilder(PgTestSchema.ServerUrl!) { SearchPath = _live.Name }
            .ConnectionString;

        _schemaDir = Path.Combine(
            Path.GetTempPath(), "schemorph-pg-ck-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(Path.Combine(_schemaDir, "tables"));
        await File.WriteAllTextAsync(Path.Combine(_schemaDir, "tables", "work_request.sql"), $"""
            CREATE TABLE "{_live.Name}".work_request (
                id uuid NOT NULL,
                status varchar(20) NOT NULL DEFAULT 'draft',
                CONSTRAINT pk_work_request PRIMARY KEY (id),
                CONSTRAINT ck_work_request_status CHECK (status IN ('draft', 'submitted', 'approved'))
            );
            """);
    }

    public async Task DisposeAsync()
    {
        await _live.DisposeAsync();
        try { Directory.Delete(_schemaDir, recursive: true); } catch { }
    }

    [SkippableFact]
    public async Task Varchar_check_in_list_converges_after_apply()
    {
        var apply = await ApplyOperation.RunAsync(_provider, _ledger,
            new ApplyOperation.Request(_schemaDir, _url, ExpectedPlanHash: null));
        Assert.True(apply.Success, string.Join("; ", apply.Errors.Select(e => e.Text)));

        // The convergence contract: what apply wrote is what diff reads back.
        var rediff = await DiffOperation.RunAsync(_provider, _ledger, _schemaDir, _url, allowDestructive: false);
        Assert.True(rediff.Success, string.Join("; ", rediff.Errors.Select(e => e.Text)));
        Assert.False(rediff.Plan!.HasChanges,
            "varchar CHECK must not re-plan as a perpetual alter after apply");

        // Stability: a second apply is a no-op and the loop stays empty.
        var applyAgain = await ApplyOperation.RunAsync(_provider, _ledger,
            new ApplyOperation.Request(_schemaDir, _url, ExpectedPlanHash: null));
        Assert.True(applyAgain.Success);
        Assert.Empty(applyAgain.Applied);

        var rediffAgain = await DiffOperation.RunAsync(_provider, _ledger, _schemaDir, _url, allowDestructive: false);
        Assert.False(rediffAgain.Plan!.HasChanges);
    }
}
