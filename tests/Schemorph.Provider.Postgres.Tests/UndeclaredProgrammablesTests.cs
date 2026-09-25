using Npgsql;
using Schemorph.Core.Operations;
using Schemorph.Core.Planning;

namespace Schemorph.Provider.Postgres.Tests;

/// <summary>
/// Deleting a programmable object's file is how the desired state says to drop it — the
/// same contract the SQL Server provider's engine carries out for every object not in
/// source. The PostgreSQL comparison read tables only, so a deleted view file left the
/// view behind, invisible to diff and status alike, and a table it read could then not
/// be dropped: the approved plan failed at apply time with SQLSTATE 2BP01.
/// </summary>
public class UndeclaredProgrammablesTests : IAsyncLifetime
{
    private PgTestSchema _live = null!;
    private string _url = null!;
    private string _schemaDir = null!;
    private readonly PostgresProvider _provider = new();
    private readonly PostgresLedgerStore _ledger = new();

    private const string LiveV1 = """
        CREATE TABLE "Orders" (
            "Id" integer NOT NULL,
            "Amount" numeric NOT NULL,
            CONSTRAINT "PK_Orders" PRIMARY KEY ("Id")
        );
        INSERT INTO "Orders" VALUES (1, 10);
        CREATE VIEW "BigOrders" AS SELECT "Id", "Amount" FROM "Orders" WHERE "Amount" > 5;
        CREATE VIEW "BigOrderIds" AS SELECT "Id" FROM "BigOrders";
        CREATE FUNCTION "Doubled"(x numeric) RETURNS numeric LANGUAGE sql AS 'SELECT x * 2';
        CREATE FUNCTION "Doubled"(x integer) RETURNS integer LANGUAGE sql AS 'SELECT x * 2';
        """;

    public async Task InitializeAsync()
    {
        _live = await PgTestSchema.CreateAsync(LiveV1);
        _url = new NpgsqlConnectionStringBuilder(PgTestSchema.ServerUrl!) { SearchPath = _live.Name }
            .ConnectionString;
        _schemaDir = Path.Combine(Path.GetTempPath(), "schemorph-pg-undeclared-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(Path.Combine(_schemaDir, "tables"));
        Directory.CreateDirectory(Path.Combine(_schemaDir, "views"));
    }

    public async Task DisposeAsync()
    {
        await _live.DisposeAsync();
        try { Directory.Delete(_schemaDir, recursive: true); } catch { }
    }

    private Task WriteOrders() => File.WriteAllTextAsync(Path.Combine(_schemaDir, "tables", "Orders.sql"), $"""
        CREATE TABLE "{_live.Name}"."Orders" (
            "Id" integer NOT NULL,
            "Amount" numeric NOT NULL,
            CONSTRAINT "PK_Orders" PRIMARY KEY ("Id")
        );
        """);

    private Task WriteBigOrders() => File.WriteAllTextAsync(Path.Combine(_schemaDir, "views", "BigOrders.sql"), """
        CREATE VIEW "BigOrders" AS SELECT "Id", "Amount" FROM "Orders" WHERE "Amount" > 5;
        """);

    private async Task<long> Count(string sql)
    {
        await using var connection = new NpgsqlConnection(_url);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private Task<long> Views(string name) => Count(
        $"SELECT count(*) FROM pg_views WHERE schemaname = '{_live.Name}' AND viewname = '{name}'");

    [SkippableFact]
    public async Task Objects_no_file_declares_are_planned_as_drops_and_the_plan_converges()
    {
        // Keep the table and one view; the other view and both overloads have no file.
        await WriteOrders();
        await WriteBigOrders();

        var diff = await DiffOperation.RunAsync(_provider, _ledger, _schemaDir, _url, allowDestructive: false);
        Assert.True(diff.Success, string.Join("; ", diff.Errors.Select(e => e.Text)));

        var view = Assert.Single(diff.Plan!.Actions, a => a.ObjectName == "BigOrderIds");
        Assert.Equal(PlanOperation.Drop, view.Operation);
        Assert.Equal(RiskLevel.Warning, view.Risk);   // no data lives in it — not gated
        var function = Assert.Single(diff.Plan.Actions, a => a.ObjectName == "Doubled");
        Assert.Equal(2, function.StatementCount);      // each overload by its own signature
        Assert.DoesNotContain(diff.Plan.Actions, a => a.ObjectName == "BigOrders" && a.Operation == PlanOperation.Drop);

        var outcome = await ApplyOperation.RunAsync(_provider, _ledger,
            new ApplyOperation.Request(_schemaDir, _url, ExpectedPlanHash: PlanFingerprint.Compute(diff.Plan)));
        Assert.True(outcome.Success, string.Join("; ", outcome.Errors.Select(e => e.Text)));

        Assert.Equal(0, await Views("BigOrderIds"));
        Assert.Equal(1, await Views("BigOrders"));
        Assert.Equal(0, await Count(
            $"SELECT count(*) FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace " +
            $"WHERE n.nspname = '{_live.Name}' AND p.proname = 'Doubled'"));

        // The redefine strategy's history says the dropped objects are gone.
        var tombstones = (await _ledger.ReadAsync(_url, "redefine"))
            .Where(e => e.Operation == "Drop").Select(e => e.ObjectName).ToList();
        Assert.Contains("BigOrderIds", tombstones);
        Assert.Contains("Doubled", tombstones);

        var rediff = await DiffOperation.RunAsync(_provider, _ledger, _schemaDir, _url, allowDestructive: false);
        Assert.True(rediff.Success, string.Join("; ", rediff.Errors.Select(e => e.Text)));
        Assert.Empty(rediff.Plan!.Actions);
    }

    /// <summary>
    /// The case that failed at apply time: a table is dropped while views read it. The
    /// views go first — the one reading the other ahead of it — and the drop of the table
    /// the approved plan contains now runs.
    /// </summary>
    [SkippableFact]
    public async Task Views_reading_a_dropped_table_are_dropped_ahead_of_it()
    {
        // An empty desired state: no files for anything.
        var diff = await DiffOperation.RunAsync(_provider, _ledger, _schemaDir, _url, allowDestructive: true);
        Assert.True(diff.Success, string.Join("; ", diff.Errors.Select(e => e.Text)));

        var order = diff.Plan!.Actions.Select(a => a.ObjectName).ToList();
        Assert.True(order.IndexOf("BigOrderIds") < order.IndexOf("BigOrders"), string.Join(", ", order));
        Assert.True(order.IndexOf("BigOrders") < order.IndexOf("Orders"), string.Join(", ", order));

        var outcome = await ApplyOperation.RunAsync(_provider, _ledger,
            new ApplyOperation.Request(_schemaDir, _url, AllowDestructive: true,
                ExpectedPlanHash: PlanFingerprint.Compute(diff.Plan)));
        Assert.True(outcome.Success, string.Join("; ", outcome.Errors.Select(e => e.Text)));

        Assert.Equal(0, await Views("BigOrders"));
        Assert.Equal(0, await Count(
            $"SELECT count(*) FROM information_schema.tables WHERE table_schema = '{_live.Name}' AND table_name = 'Orders'"));
    }

    /// <summary>
    /// What <c>status</c> reports is the plan a diff would produce, so an undeclared view is
    /// drift there too — the "No drift" it used to report is what hid it.
    /// </summary>
    [SkippableFact]
    public async Task Status_reports_an_undeclared_view_as_drift()
    {
        await WriteOrders();
        await WriteBigOrders();

        var status = await StatusOperation.RunAsync(_provider, _ledger, new StatusOperation.Request(_schemaDir, _url));
        Assert.True(status.Success, string.Join("; ", status.Errors.Select(e => e.Text)));
        Assert.True(status.Status!.HasPendingWork);
        Assert.Contains(status.Status.Plan.Actions, a => a.ObjectName == "BigOrderIds" && a.Operation == PlanOperation.Drop);
    }
}
