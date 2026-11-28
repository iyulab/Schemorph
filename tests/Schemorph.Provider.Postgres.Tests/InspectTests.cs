using Schemorph.Core.Providers;

namespace Schemorph.Provider.Postgres.Tests;

public class InspectTests
{
    private const string Ddl = """
        CREATE TABLE "Workspaces" (
            "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
            "Name" text NOT NULL,
            CONSTRAINT "PK_Workspaces" PRIMARY KEY ("Id"),
            CONSTRAINT "UQ_Workspaces_Name" UNIQUE ("Name")
        );
        """;

    [SkippableFact]
    public async Task Inspect_renders_the_live_schema_as_desired_state_files()
    {
        await using var schema = await PostgresTestSchema.CreateAsync(Ddl);
        var url = $"{PostgresTestSchema.ServerUrl};Search Path={schema.Name}";

        var result = await new PostgresProvider().InspectAsync(new InspectRequest(url));

        var file = Assert.Single(result.Files);
        Assert.Equal($"tables/{schema.Name}.Workspaces.sql", file.RelativePath);
        Assert.Contains("CREATE TABLE", file.Content);
        Assert.Contains("\"Id\" uuid NOT NULL DEFAULT gen_random_uuid()", file.Content);
        Assert.Contains("ADD CONSTRAINT \"PK_Workspaces\" PRIMARY KEY", file.Content);
    }

    [SkippableFact]
    public async Task What_inspect_renders_can_be_applied_to_an_empty_schema()
    {
        // The honest test of a renderer: its output is valid SQL that recreates
        // what it read. Nothing in the read-only scope applies anything, so this drives psql-level
        // execution directly — it verifies the rendering, not an apply path.
        await using var source = await PostgresTestSchema.CreateAsync(Ddl);
        var url = $"{PostgresTestSchema.ServerUrl};Search Path={source.Name}";
        var rendered = (await new PostgresProvider().InspectAsync(new InspectRequest(url))).Files;

        await using var target = await PostgresTestSchema.CreateAsync("SELECT 1;");
        foreach (var file in rendered)
        {
            // The rendering is schema-qualified against the source schema, so it
            // recreates the source's shape; applying it into a second schema
            // proves only that the statements parse and execute.
            await PostgresTestSchema.ExecuteAsync(file.Content.Replace(
                $"\"{source.Name}\".", $"\"{target.Name}\".", StringComparison.Ordinal));
        }

        var reread = await CatalogReader.ReadTablesAsync(PostgresTestSchema.ServerUrl!, target.Name);
        var table = Assert.Single(reread);
        Assert.Equal("Workspaces", table.Name);
        Assert.Equal(2, table.Columns.Count);
        Assert.Contains(table.Constraints, c => c.Name.StartsWith("PK_", StringComparison.Ordinal));
    }

    [SkippableFact]
    public async Task Inspect_is_no_longer_refused()
    {
        await using var schema = await PostgresTestSchema.CreateAsync(Ddl);
        var url = $"{PostgresTestSchema.ServerUrl};Search Path={schema.Name}";

        // The declared capability and the behaviour have to agree; this is the
        // other half of ProviderBoundaryTests.
        var result = await new PostgresProvider().InspectAsync(new InspectRequest(url));

        Assert.NotEmpty(result.Files);
        Assert.Contains("inspect", PostgresProvider.DeclaredCapabilities);
    }
}

/// <summary>
/// Inspect is the way to adopt a database, and an undeclared programmable object is planned as a drop.
/// So inspect has to write every object the comparison would otherwise drop, or adopting a database
/// with views and routines plans the removal of all of them.
/// </summary>
public sealed class InspectProgrammablesTests : IAsyncLifetime
{
    private const string Schema = """
        CREATE TABLE "Orders" ("Id" integer NOT NULL, "Total" numeric NOT NULL, "Touched" timestamptz,
            CONSTRAINT "PK_Orders" PRIMARY KEY ("Id"));
        CREATE VIEW "BigOrders" WITH (security_barrier) AS SELECT "Id", "Total" FROM "Orders" WHERE "Total" > 100;
        CREATE VIEW "BigOrderIds" AS SELECT "Id" FROM "BigOrders";
        CREATE FUNCTION "Doubled"(x numeric) RETURNS numeric LANGUAGE sql IMMUTABLE AS $$ SELECT x * 2 $$;
        CREATE PROCEDURE "Close"(IN id integer) LANGUAGE plpgsql AS $$ BEGIN DELETE FROM "Orders" WHERE "Id" = id; END $$;
        CREATE FUNCTION "Touch"() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN NEW."Touched" := now(); RETURN NEW; END $$;
        CREATE TRIGGER "Orders_Touch" BEFORE UPDATE ON "Orders" FOR EACH ROW EXECUTE FUNCTION "Touch"();
        CREATE MATERIALIZED VIEW "OrderTotals" AS SELECT sum("Total") AS total FROM "Orders";
        """;

    private PostgresTestSchema _live = null!;
    private string _url = null!;
    private string _dir = null!;
    private readonly PostgresProvider _provider = new();
    private readonly PostgresLedgerStore _ledger = new();

    public async Task InitializeAsync()
    {
        _live = await PostgresTestSchema.CreateAsync(Schema);
        _url = $"{PostgresTestSchema.ServerUrl};Search Path={_live.Name}";
        _dir = Path.Combine(Path.GetTempPath(), "schemorph-pg-inspect-" + Guid.NewGuid().ToString("n")[..8]);
    }

    public async Task DisposeAsync()
    {
        await _live.DisposeAsync();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [SkippableFact]
    public async Task Inspect_writes_views_routines_and_triggers_and_leaves_out_what_is_never_declared()
    {
        var result = await _provider.InspectAsync(new InspectRequest(_url));

        Assert.Equal(
            [
                $"functions/{_live.Name}.Doubled.sql",
                $"functions/{_live.Name}.Touch.sql",
                $"procedures/{_live.Name}.Close.sql",
                $"tables/{_live.Name}.Orders.sql",
                $"triggers/{_live.Name}.Orders.Orders_Touch.sql",
                $"views/{_live.Name}.BigOrderIds.sql",
                $"views/{_live.Name}.BigOrders.sql",
            ],
            result.Files.Select(f => f.RelativePath).OrderBy(p => p, StringComparer.Ordinal));
        Assert.Contains("WITH (security_barrier=true)", result.Files.Single(f => f.RelativePath.EndsWith("BigOrders.sql")).Content);
        Assert.All(result.Files.Where(f => !f.RelativePath.StartsWith("tables/", StringComparison.Ordinal)),
            f => Assert.EndsWith(";\n", f.Content));
    }

    [SkippableFact]
    public async Task What_inspect_writes_is_adopted_without_a_single_drop_and_then_converges()
    {
        await Schemorph.Core.Operations.InspectOperation.RunAsync(_provider, _url, _dir);

        var first = await Schemorph.Core.Operations.DiffOperation.RunAsync(_provider, _ledger, _dir, _url, allowDestructive: false);
        Assert.True(first.Success, string.Join("; ", first.Errors.Select(e => $"{e.Code}: {e.Text}")));
        Assert.DoesNotContain(first.Plan!.Actions, a => a.Operation == Schemorph.Core.Planning.PlanOperation.Drop);

        var applied = await Schemorph.Core.Operations.ApplyOperation.RunAsync(_provider, _ledger,
            new Schemorph.Core.Operations.ApplyOperation.Request(_dir, _url));
        Assert.True(applied.Success, string.Join("; ", applied.Errors.Select(e => $"{e.Code}: {e.Text}")));

        var again = await Schemorph.Core.Operations.DiffOperation.RunAsync(_provider, _ledger, _dir, _url, allowDestructive: false);
        Assert.True(again.Success, string.Join("; ", again.Errors.Select(e => $"{e.Code}: {e.Text}")));
        Assert.True(again.Plan!.Actions.Count == 0,
            string.Join(", ", again.Plan.Actions.Select(a => $"{a.Operation} {a.ObjectName}")));
    }

    [SkippableFact]
    public async Task A_name_with_several_overloads_gets_a_file_per_overload_and_is_adopted_whole()
    {
        await PostgresTestSchema.ExecuteAsync($"""
            SET search_path TO "{_live.Name}";
            CREATE FUNCTION "Doubled"(x integer) RETURNS integer LANGUAGE sql IMMUTABLE AS $$ SELECT x * 2 $$;
            """);

        var result = await _provider.InspectAsync(new InspectRequest(_url));
        Assert.Equal(
            [$"functions/{_live.Name}.Doubled(x integer).sql", $"functions/{_live.Name}.Doubled(x numeric).sql"],
            result.Files.Select(f => f.RelativePath).Where(p => p.Contains(".Doubled")).OrderBy(p => p, StringComparer.Ordinal));

        await Schemorph.Core.Operations.InspectOperation.RunAsync(_provider, _url, _dir);
        var plan = await Schemorph.Core.Operations.DiffOperation.RunAsync(_provider, _ledger, _dir, _url, allowDestructive: false);
        Assert.True(plan.Success, string.Join("; ", plan.Errors.Select(e => $"{e.Code}: {e.Text}")));
        Assert.DoesNotContain(plan.Plan!.Actions, a => a.Operation == Schemorph.Core.Planning.PlanOperation.Drop);
    }
}
