using Npgsql;
using Schemorph.Core.Operations;

namespace Schemorph.Provider.Postgres.Tests;

/// <summary>
/// The comparison builds the desired state in a scratch schema that holds the desired tables and,
/// for probing views, the declared routines — nothing else. A reference qualified to the target
/// schema therefore means two different things: to a declared table or routine it means the
/// desired one (in the scratch schema), to anything else — a type or function an extension
/// installed there — it means the one that exists there, as it will when apply runs. Two faults
/// came from treating both alike:
/// <list type="bullet">
/// <item>A view changed to call a function the same plan creates, written with the schema, was
/// probed against the target schema, where the function did not exist yet — the diff stopped with
/// SCHEMORPH013 although the state applies to an empty database.</item>
/// <item>A column of an extension's type was read back from the scratch schema with the type
/// qualified (it is not on that schema's search path) and from the target schema bare, so every
/// diff planned the same type change again; written with the schema, the type was moved into the
/// scratch schema, where it does not exist (42704).</item>
/// </list>
/// </summary>
/// <remarks>
/// One class on purpose: an extension is installed once per database, and xUnit runs the tests of
/// a class one at a time. Each test that installs <c>citext</c> puts it in its own schema, and
/// dropping that schema drops the extension with it.
/// </remarks>
public sealed class TargetSchemaReferencesInShadowTests
{
    private static async Task<(PostgresTestSchema Live, string Url)> LiveAsync(string? extension = null)
    {
        var live = await PostgresTestSchema.CreateAsync("SELECT 1;");
        if (extension is not null)
        {
            await PostgresTestSchema.ExecuteAsync($"""CREATE EXTENSION {extension} SCHEMA "{live.Name}";""");
        }
        var url = new NpgsqlConnectionStringBuilder(PostgresTestSchema.ServerUrl!) { SearchPath = live.Name }
            .ConnectionString;
        return (live, url);
    }

    private sealed class DesiredDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "schemorph-pg-target-" + Guid.NewGuid().ToString("n")[..8]);

        public DesiredDir()
        {
            foreach (var kind in new[] { "tables", "views", "functions" })
            {
                Directory.CreateDirectory(System.IO.Path.Combine(Path, kind));
            }
        }

        public async Task WriteAsync(string relative, string sql)
            => await File.WriteAllTextAsync(System.IO.Path.Combine(Path, relative), sql);

        public void Delete(string relative) => File.Delete(System.IO.Path.Combine(Path, relative));

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { }
        }
    }

    private static async Task ApplyAsync(string dir, string url)
    {
        var outcome = await ApplyOperation.RunAsync(
            new PostgresProvider(), new PostgresLedgerStore(), new ApplyOperation.Request(dir, url));
        Assert.True(outcome.Success, string.Join("; ", outcome.Errors.Select(e => $"{e.Code}: {e.Text}")));
    }

    private static async Task<bool> HasChangesAsync(string dir, string url)
    {
        var diff = await DiffOperation.RunAsync(new PostgresProvider(), new PostgresLedgerStore(), dir, url, false);
        Assert.True(diff.Success, string.Join("; ", diff.Errors.Select(e => $"{e.Code}: {e.Text}")));
        return diff.Plan!.HasChanges;
    }

    [SkippableTheory]
    [InlineData("f_new(x integer)", "f_new(t.id)")]          // a new function replaces the old one
    [InlineData("f_old(x integer, y integer)", "f_old(t.id, 1)")]   // the same name with other arguments
    public async Task A_view_moved_to_a_function_the_same_plan_creates_is_planned_and_converges(
        string newSignature, string newCall)
    {
        var (live, url) = await LiveAsync();
        await using var _ = live;
        using var dir = new DesiredDir();
        var s = live.Name;

        await dir.WriteAsync("tables/t.sql", $"""CREATE TABLE "{s}".t (id integer NOT NULL, CONSTRAINT pk_t PRIMARY KEY (id));""");
        await dir.WriteAsync("functions/f_old.sql",
            $"""CREATE OR REPLACE FUNCTION "{s}".f_old(x integer) RETURNS boolean LANGUAGE sql STABLE AS $$ SELECT x > 0 $$;""");
        await dir.WriteAsync("views/v.sql",
            $"""CREATE OR REPLACE VIEW "{s}".v AS SELECT t.id FROM "{s}".t t WHERE "{s}".f_old(t.id);""");
        await ApplyAsync(dir.Path, url);

        dir.Delete("functions/f_old.sql");
        await dir.WriteAsync("functions/f_next.sql",
            $"""CREATE OR REPLACE FUNCTION "{s}".{newSignature} RETURNS boolean LANGUAGE sql STABLE AS $$ SELECT x > 1 $$;""");
        await dir.WriteAsync("views/v.sql",
            $"""CREATE OR REPLACE VIEW "{s}".v AS SELECT t.id FROM "{s}".t t WHERE "{s}".{newCall};""");

        Assert.True(await HasChangesAsync(dir.Path, url));
        await ApplyAsync(dir.Path, url);
        Assert.False(await HasChangesAsync(dir.Path, url));

        // No file declares f_old(integer) any more — under either new signature it is gone, not
        // left behind beside the overload that replaced it.
        await using var connection = new NpgsqlConnection(url);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"""
            SELECT count(*) FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
            WHERE n.nspname = '{s}' AND p.proname = 'f_old' AND p.proargtypes = '23'::oidvector
            """, connection);
        Assert.Equal(0L, (long)(await command.ExecuteScalarAsync())!);
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_column_of_an_extension_type_converges_written_bare_or_with_the_schema(bool qualified)
    {
        var (live, url) = await LiveAsync("citext");
        await using var _ = live;
        using var dir = new DesiredDir();
        var type = qualified ? $"\"{live.Name}\".citext" : "citext";

        await dir.WriteAsync("tables/emb.sql", $"""
            CREATE TABLE emb (id uuid NOT NULL, v {type} NOT NULL, CONSTRAINT pk_emb PRIMARY KEY (id));
            CREATE INDEX ix_emb_v ON emb (v {(qualified ? $"\"{live.Name}\"." : "")}citext_pattern_ops);
            """);

        Assert.True(await HasChangesAsync(dir.Path, url));
        await ApplyAsync(dir.Path, url);
        Assert.False(await HasChangesAsync(dir.Path, url));
    }
}
