using Npgsql;
using Schemorph.Core.Operations;
using Schemorph.Core.Planning;

namespace Schemorph.Provider.Postgres.Tests;

/// <summary>
/// A routine file declares one signature, not every overload that shares its name. Matched by
/// name, a file whose argument list changed only added the new overload: the old one stayed live
/// and every diff called the state converged. A live overload of a declared name whose argument
/// types differ from the file's is now undeclared like any other — planned as a drop under its
/// own signature — and the engine decides what "the same types" means, so spelling a type another
/// way (<c>int</c> for <c>integer</c>) is not a difference. Where the types cannot be settled
/// before apply, nothing is dropped.
/// </summary>
public sealed class RoutineOverloadIdentityTests : IAsyncLifetime
{
    private PostgresTestSchema _live = null!;
    private string _url = null!;
    private string _dir = null!;
    private readonly PostgresProvider _provider = new();
    private readonly PostgresLedgerStore _ledger = new();

    public async Task InitializeAsync()
    {
        _live = await PostgresTestSchema.CreateAsync("SELECT 1;");
        _url = new NpgsqlConnectionStringBuilder(PostgresTestSchema.ServerUrl!) { SearchPath = _live.Name }
            .ConnectionString;
        _dir = Path.Combine(Path.GetTempPath(), "schemorph-pg-overload-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(Path.Combine(_dir, "tables"));
        Directory.CreateDirectory(Path.Combine(_dir, "functions"));
    }

    public async Task DisposeAsync()
    {
        await _live.DisposeAsync();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private Task WriteAsync(string relative, string sql)
        => File.WriteAllTextAsync(Path.Combine(_dir, relative), sql);

    private async Task ApplyAsync()
    {
        var outcome = await ApplyOperation.RunAsync(_provider, _ledger, new ApplyOperation.Request(_dir, _url));
        Assert.True(outcome.Success, string.Join("; ", outcome.Errors.Select(e => $"{e.Code}: {e.Text}")));
    }

    private async Task<Plan> DiffAsync()
    {
        var diff = await DiffOperation.RunAsync(_provider, _ledger, _dir, _url, allowDestructive: false);
        Assert.True(diff.Success, string.Join("; ", diff.Errors.Select(e => $"{e.Code}: {e.Text}")));
        return diff.Plan!;
    }

    private async Task<List<string>> OverloadsAsync(string name)
    {
        await using var connection = new NpgsqlConnection(_url);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT pg_get_function_identity_arguments(p.oid) FROM pg_proc p
            JOIN pg_namespace n ON n.oid = p.pronamespace
            WHERE n.nspname = @schema AND p.proname = @name ORDER BY 1
            """, connection);
        command.Parameters.AddWithValue("schema", _live.Name);
        command.Parameters.AddWithValue("name", name);
        var result = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) result.Add(reader.GetString(0));
        return result;
    }

    private Task AddByHandAsync(string sql)
        => PostgresTestSchema.ExecuteAsync($"SET search_path TO \"{_live.Name}\"; {sql}");

    [SkippableFact]
    public async Task A_signature_spelled_with_other_type_names_is_the_same_routine_and_converges()
    {
        // Aliases, a typmod, an array, VARIADIC, OUT parameters of a function (not part of its
        // identity) and of a procedure (part of it) — each spelled differently from the catalog.
        await WriteAsync("functions/g.sql", """
            CREATE FUNCTION g(a int, b varchar(10), c text[], VARIADIC d int4[]) RETURNS int8
            LANGUAGE sql AS $$ SELECT 1::bigint $$;
            """);
        await WriteAsync("functions/h.sql", """
            CREATE FUNCTION h(IN a double precision, OUT b timestamp with time zone)
            LANGUAGE sql AS $$ SELECT now() $$;
            """);
        await WriteAsync("functions/p.sql", """
            CREATE PROCEDURE p(IN a smallint, OUT b character varying)
            LANGUAGE plpgsql AS $$ BEGIN b := 'x'; END $$;
            """);
        await ApplyAsync();

        var actions = (await DiffAsync()).Actions;
        Assert.True(actions.Count == 0, string.Join(", ", actions.Select(a => $"{a.Operation} {a.ObjectName}")));
        Assert.Single(await OverloadsAsync("g"));
        Assert.Single(await OverloadsAsync("h"));
        Assert.Single(await OverloadsAsync("p"));
    }

    [SkippableFact]
    public async Task An_overload_added_by_hand_beside_a_declared_routine_is_planned_as_a_drop_of_that_signature()
    {
        await WriteAsync("functions/g.sql", "CREATE FUNCTION g(a integer) RETURNS integer LANGUAGE sql AS $$ SELECT a $$;");
        await ApplyAsync();
        await AddByHandAsync("CREATE FUNCTION g(a text) RETURNS text LANGUAGE sql AS $$ SELECT a $$;");

        var plan = await DiffAsync();
        var drop = Assert.Single(plan.Actions);
        Assert.Equal(PlanOperation.Drop, drop.Operation);
        Assert.Equal("g(a text)", drop.ObjectName);

        await ApplyAsync();
        Assert.Equal(["a integer"], await OverloadsAsync("g"));
        Assert.Empty((await DiffAsync()).Actions);
    }

    [SkippableFact]
    public async Task An_argument_type_written_as_a_column_type_reference_leaves_every_overload_alone()
    {
        // %TYPE is resolved by the engine only when the function is created; before apply the
        // comparison cannot tell which overload the file means, so it claims none.
        await WriteAsync("tables/t.sql", "CREATE TABLE t (id integer NOT NULL, CONSTRAINT pk_t PRIMARY KEY (id));");
        await WriteAsync("functions/k.sql", "CREATE FUNCTION k(x t.id%TYPE) RETURNS integer LANGUAGE sql AS $$ SELECT x $$;");
        await ApplyAsync();
        await AddByHandAsync("CREATE FUNCTION k(x text) RETURNS integer LANGUAGE sql AS $$ SELECT 1 $$;");

        Assert.Empty((await DiffAsync()).Actions);
        Assert.Equal(2, (await OverloadsAsync("k")).Count);
    }

    [SkippableFact]
    public async Task An_old_overload_something_other_than_a_view_still_uses_is_kept_and_named()
    {
        // A CHECK constraint holds on to the overload it was created with. Dropping that overload
        // ahead of the table statements fails the apply with 2BP01, so it is kept — and said so,
        // since the diff no longer describes the database exactly.
        // The starting state is made by hand and adopted: a table whose CHECK calls a function the same
        // desired state declares does not build in the comparison's scratch schema from an empty database.
        const string function = "CREATE FUNCTION f(x integer) RETURNS boolean LANGUAGE sql IMMUTABLE AS $$ SELECT x > 0 $$;";
        const string table = "CREATE TABLE t (x integer NOT NULL, CONSTRAINT ck_t CHECK (f(x)));";
        await AddByHandAsync(function + table);
        await WriteAsync("functions/f.sql", function);
        await WriteAsync("tables/t.sql", table);
        await ApplyAsync();

        await WriteAsync("functions/f.sql",
            "CREATE FUNCTION f(x integer, y integer DEFAULT 0) RETURNS boolean LANGUAGE sql IMMUTABLE AS $$ SELECT x > y $$;");

        var plan = await DiffAsync();
        Assert.DoesNotContain(plan.Actions, a => a.Operation == PlanOperation.Drop);
        var kept = Assert.Single(plan.Messages, m => m.Code == "SCHEMORPH018");
        Assert.Contains("f(x integer)", kept.Text);
        Assert.Contains("ck_t", kept.Text);

        await ApplyAsync();
        Assert.Equal(["x integer", "x integer, y integer"], await OverloadsAsync("f"));
    }

    [SkippableFact]
    public async Task A_declared_signature_missing_live_is_re_created_though_its_name_has_another_overload()
    {
        // The file is unchanged, so the ledger has nothing to re-apply; what is live is another overload of the name.
        await WriteAsync("functions/g.sql", "CREATE FUNCTION g(a integer) RETURNS integer LANGUAGE sql AS $$ SELECT a $$;");
        await ApplyAsync();
        await AddByHandAsync("DROP FUNCTION g(integer); CREATE FUNCTION g(a text) RETURNS text LANGUAGE sql AS $$ SELECT a $$;");

        var plan = await DiffAsync();
        Assert.Contains(plan.Actions, a => a.Operation == PlanOperation.Drop && a.ObjectName == "g(a text)");
        Assert.Contains(plan.Actions, a => a.ObjectName == "g" && a.Operation != PlanOperation.Drop);

        await ApplyAsync();
        Assert.Equal(["a integer"], await OverloadsAsync("g"));
        Assert.Empty((await DiffAsync()).Actions);
    }

    [SkippableFact]
    public async Task An_unchanged_view_still_calling_the_old_signature_fails_the_apply_whole()
    {
        // The desired state calls f(integer) from a view and no longer declares it — it would not apply to an empty
        // database either. The apply is one transaction: it fails, and the old overload and the view are still there.
        await WriteAsync("tables/t.sql", "CREATE TABLE t (id integer NOT NULL, CONSTRAINT pk_t PRIMARY KEY (id));");
        await WriteAsync("functions/f.sql", "CREATE FUNCTION f(x integer) RETURNS boolean LANGUAGE sql STABLE AS $$ SELECT x > 0 $$;");
        Directory.CreateDirectory(Path.Combine(_dir, "views"));
        await WriteAsync("views/v.sql", "CREATE VIEW v AS SELECT t.id FROM t WHERE f(t.id);");
        await ApplyAsync();

        await WriteAsync("functions/f.sql",
            "CREATE FUNCTION f(x integer, y integer) RETURNS boolean LANGUAGE sql STABLE AS $$ SELECT x > y $$;");

        var outcome = await ApplyOperation.RunAsync(_provider, _ledger, new ApplyOperation.Request(_dir, _url));
        Assert.False(outcome.Success);
        Assert.Equal(["x integer"], await OverloadsAsync("f"));
    }

    [Fact]
    public void Only_a_declared_view_calling_the_dropped_overload_goes_first()
    {
        var undeclared = new[]
        {
            new PostgresLiveProgrammables.Undeclared("f(x integer)", "ScalarFunction",
                ["DROP FUNCTION f(integer);"], ["f(x integer)"]),
        };
        var calls = new[]
        {
            new PostgresLiveProgrammables.ViewCall("calls_old", "f", "f(x integer)"),
            new PostgresLiveProgrammables.ViewCall("calls_kept", "f", "f(x integer, y integer)"),
        };

        var dropped = PostgresProvider.ViewsDroppedFirst(
            [], [], new HashSet<string>(["calls_old", "calls_kept"], StringComparer.Ordinal), undeclared, [], calls);

        Assert.Equal(["calls_old"], dropped);
    }
}
