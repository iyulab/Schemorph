using Npgsql;
using Schemorph.Core.Ledger;

namespace Schemorph.Provider.Postgres.Tests;

/// <summary>
/// The ledger's Postgres dialect, and the transactional coupling ADR-0004 §2
/// requires of it: a script and its ledger rows are one unit. The ledger lives
/// in the connection's target schema — the R1 baseline owner may have no
/// rights anywhere else.
/// </summary>
public class PostgresLedgerTests
{
    private static string UrlFor(string schema)
        => new NpgsqlConnectionStringBuilder(PgTestSchema.ServerUrl!) { SearchPath = schema }
            .ConnectionString;

    [SkippableFact]
    public async Task Initialization_is_idempotent_and_rows_round_trip()
    {
        await using var schema = await PgTestSchema.CreateAsync("SELECT 1;");
        var url = UrlFor(schema.Name);
        var store = new PostgresLedgerStore();

        await store.EnsureInitializedAsync(url);
        await store.EnsureInitializedAsync(url);   // second call must be a no-op

        await store.AppendAsync(url,
        [
            new LedgerEntry("migration", "V0001__init.sql", "apply", "abc123", true, null),
            new LedgerEntry("redefine", "public.VOrders", "redefine", "def456", false, "boom"),
        ]);

        var migrations = await store.ReadAsync(url, "migration");
        var entry = Assert.Single(migrations);
        Assert.Equal("V0001__init.sql", entry.ObjectName);
        Assert.True(entry.Succeeded);
        Assert.NotNull(entry.AppliedAtUtc);   // the database stamps it

        var redefines = await store.ReadAsync(url, "redefine");
        Assert.Equal("boom", Assert.Single(redefines).Detail);
    }

    [SkippableFact]
    public async Task A_fresh_schema_reads_as_no_history()
    {
        await using var schema = await PgTestSchema.CreateAsync("SELECT 1;");

        Assert.Empty(await new PostgresLedgerStore().ReadAsync(UrlFor(schema.Name), "migration"));
    }

    [SkippableFact]
    public async Task Script_and_ledger_commit_together_or_not_at_all()
    {
        await using var schema = await PgTestSchema.CreateAsync("SELECT 1;");
        var url = UrlFor(schema.Name);
        var store = new PostgresLedgerStore();
        await store.EnsureInitializedAsync(url);

        var goodScript = $"CREATE TABLE \"{schema.Name}\".\"FromScript\" (\"x\" integer);";
        await PgScriptExecutor.ExecuteAsync(url, goodScript,
            [new LedgerEntry("migration", "V0002__t.sql", "apply", "aaa", true, null)]);
        Assert.Single(await store.ReadAsync(url, "migration"));

        // A failing script must leave NEITHER its effects nor its ledger row.
        var badScript = $"CREATE TABLE \"{schema.Name}\".\"Half\" (\"x\" integer); SELECT 1/0;";
        await Assert.ThrowsAsync<PostgresException>(() => PgScriptExecutor.ExecuteAsync(
            url, badScript,
            [new LedgerEntry("migration", "V0003__bad.sql", "apply", "bbb", true, null)]));

        Assert.Single(await store.ReadAsync(url, "migration"));   // still just V0002
        var tables = await CatalogReader.ReadTablesAsync(PgTestSchema.ServerUrl!, schema.Name);
        Assert.DoesNotContain(tables, t => t.Name == "Half");
    }

    [SkippableFact]
    public async Task The_ledger_is_invisible_to_inspect_and_comparison()
    {
        // cycle-76's standing instruction: the moment __SchemorphHistory exists,
        // the same self-exclusion the SQL Server provider applies must hold here.
        await using var schema = await PgTestSchema.CreateAsync("CREATE TABLE \"Real\" (\"x\" integer);");
        await new PostgresLedgerStore().EnsureInitializedAsync(UrlFor(schema.Name));

        var tables = await CatalogReader.ReadTablesAsync(PgTestSchema.ServerUrl!, schema.Name);

        Assert.Single(tables);
        Assert.Equal("Real", tables[0].Name);
    }
}
