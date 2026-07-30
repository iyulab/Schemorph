using Npgsql;
using Schemorph.Core.Operations;
using Schemorph.Core.Planning;

namespace Schemorph.Provider.Postgres.Tests;

/// <summary>
/// A plan that explains itself, on the second provider too: each change carries the
/// slice of DDL attributable to it, and the safety lint that reads those slices
/// fires on the hazard it exists for. Attribution here is exact rather than
/// inferred — synthesis records the table each statement belongs to as it emits it,
/// so there is no script to parse back and no unattributable remainder.
///
/// The slices are descriptive: what executes is the whole update script. What makes
/// them load-bearing anyway is the fingerprint, which binds them — so this suite
/// also pins the diff→apply round trip that asymmetric attribution would break.
/// </summary>
public class ChangeAttributionTests : IAsyncLifetime
{
    private PgTestSchema _live = null!;
    private string _url = null!;
    private string _schemaDir = null!;
    private readonly PostgresProvider _provider = new();
    private readonly PostgresLedgerStore _ledger = new();

    private const string LiveV1 = """
        CREATE TABLE task_record (
            id uuid NOT NULL,
            title text NOT NULL,
            CONSTRAINT pk_task_record PRIMARY KEY (id)
        );
        """;

    public async Task InitializeAsync()
    {
        _live = await PgTestSchema.CreateAsync(LiveV1);
        _url = new NpgsqlConnectionStringBuilder(PgTestSchema.ServerUrl!) { SearchPath = _live.Name }
            .ConnectionString;

        _schemaDir = Path.Combine(
            Path.GetTempPath(), "schemorph-pg-attrib-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(Path.Combine(_schemaDir, "tables"));

        // An existing table gains a NOT NULL column with nothing to fill it — the
        // SCHEMORPH101 hazard, which fails on any table that already holds rows.
        await File.WriteAllTextAsync(Path.Combine(_schemaDir, "tables", "task_record.sql"), $"""
            CREATE TABLE "{_live.Name}".task_record (
                id uuid NOT NULL,
                title text NOT NULL,
                owner text NOT NULL,
                CONSTRAINT pk_task_record PRIMARY KEY (id)
            );
            """);
        // A brand-new table with the same shape is NOT the hazard: no rows exist to
        // violate the constraint.
        await File.WriteAllTextAsync(Path.Combine(_schemaDir, "tables", "task_note.sql"), $"""
            CREATE TABLE "{_live.Name}".task_note (
                id uuid NOT NULL,
                body text NOT NULL,
                CONSTRAINT pk_task_note PRIMARY KEY (id)
            );
            """);
    }

    public async Task DisposeAsync()
    {
        await _live.DisposeAsync();
        try { Directory.Delete(_schemaDir, recursive: true); } catch { }
    }

    [SkippableFact]
    public async Task Each_change_carries_its_own_slice_and_the_lint_reads_it()
    {
        var diff = await DiffOperation.RunAsync(_provider, _ledger, _schemaDir, _url, allowDestructive: false);
        Assert.True(diff.Success, string.Join("; ", diff.Errors.Select(e => e.Text)));
        var plan = diff.Plan!;

        var altered = Assert.Single(plan.Actions, a => a.ObjectName == "task_record");
        var created = Assert.Single(plan.Actions, a => a.ObjectName == "task_note");

        // Attributed, and attributed to the right change.
        Assert.Contains("ADD COLUMN", altered.Sql);
        Assert.Contains("owner", altered.Sql);
        Assert.Contains("CREATE TABLE", created.Sql);
        Assert.DoesNotContain("task_note", altered.Sql);

        // The slices explain; they are not the artifact. The whole script still is.
        Assert.NotNull(diff.UpdateScript);
        Assert.Contains("SET LOCAL search_path", diff.UpdateScript);

        // The hazard fires for the existing table only.
        var notNullWarnings = plan.Messages.Where(m => m.Code == "SCHEMORPH101").ToList();
        var warning = Assert.Single(notNullWarnings);
        Assert.Contains("task_record", warning.Text);

        // No rebuild is ever claimed: this provider alters in place.
        Assert.DoesNotContain(plan.Messages, m => m.Code == "SCHEMORPH102");
    }

    [SkippableFact]
    public async Task The_gate_accepts_the_hash_diff_advertised()
    {
        // The fingerprint binds the attribution (plan format 1.5), so apply has to
        // produce it exactly as diff did. Passing the reviewed hash back is the only
        // check that proves the two paths agree.
        var diff = await DiffOperation.RunAsync(_provider, _ledger, _schemaDir, _url, allowDestructive: false);
        var reviewed = PlanFingerprint.Compute(diff.Plan!);

        var apply = await ApplyOperation.RunAsync(_provider, _ledger,
            new ApplyOperation.Request(_schemaDir, _url, ExpectedPlanHash: reviewed));

        Assert.True(apply.Success, string.Join("; ", apply.Errors.Select(e => e.Text)));

        var rediff = await DiffOperation.RunAsync(_provider, _ledger, _schemaDir, _url, allowDestructive: false);
        Assert.False(rediff.Plan!.HasChanges);
    }
}
