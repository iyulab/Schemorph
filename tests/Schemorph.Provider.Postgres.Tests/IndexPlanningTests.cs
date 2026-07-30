using Npgsql;
using Schemorph.Core.Operations;
using Schemorph.Core.Providers;

namespace Schemorph.Provider.Postgres.Tests;

/// <summary>
/// Indexes as declared state: an index that appears, disappears or changes in the
/// desired state is planned, applied, and gone by the next diff.
///
/// The comparison reads the engine's own <c>CREATE INDEX</c> rendering, with one
/// substitution: that rendering always schema-qualifies the table it is on, even
/// with the search_path pointing at that schema, so the qualifier is removed on
/// both sides before the two are compared. Everything else is the engine's text,
/// which is what makes the aspects below visible without a parser here.
///
/// Those aspects are the reason this is not a thin wrapper over a column list. A
/// per-column projection — the obvious shape, and the one that seems sufficient
/// until it is measured — cannot see sort direction, NULLS placement, operator
/// class or collation: <c>pg_get_indexdef(oid, n, …)</c> renders a key as the bare
/// column or expression and drops every decoration, whatever the pretty flag says.
/// An index is not a set of columns; it is a set of columns under an ordering, and
/// two indexes that differ only there answer different queries.
/// </summary>
public class IndexPlanningTests : IAsyncLifetime
{
    private PgTestSchema _live = null!;
    private string _url = null!;
    private string _schemaDir = null!;
    private readonly PostgresProvider _provider = new();
    private readonly PostgresLedgerStore _ledger = new();

    // A table whose foreign-key column has no supporting index — the shape that
    // makes index management worth having, because the engine creates no index
    // for a foreign key on its own.
    private const string LiveV1 = """
        CREATE TABLE "Owner" (
            "Id" integer NOT NULL,
            CONSTRAINT "PK_Owner" PRIMARY KEY ("Id")
        );
        CREATE TABLE "Doc" (
            "Id" integer NOT NULL,
            "OwnerId" integer NOT NULL,
            "Name" text,
            "Score" integer,
            CONSTRAINT "PK_Doc" PRIMARY KEY ("Id"),
            CONSTRAINT "FK_Doc_Owner" FOREIGN KEY ("OwnerId") REFERENCES "Owner" ("Id")
        );
        """;

    public async Task InitializeAsync()
    {
        _live = await PgTestSchema.CreateAsync(LiveV1);
        _url = new NpgsqlConnectionStringBuilder(PgTestSchema.ServerUrl!) { SearchPath = _live.Name }
            .ConnectionString;
        _schemaDir = Path.Combine(
            Path.GetTempPath(), "schemorph-pg-index-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(Path.Combine(_schemaDir, "tables"));
    }

    public async Task DisposeAsync()
    {
        await _live.DisposeAsync();
        try { Directory.Delete(_schemaDir, recursive: true); } catch { }
    }

    /// <summary>Declares the two tables plus whatever index statements are given.</summary>
    private async Task DeclareAsync(params string[] indexStatements) =>
        await File.WriteAllTextAsync(Path.Combine(_schemaDir, "tables", "schema.sql"), $"""
            CREATE TABLE "{_live.Name}"."Owner" (
                "Id" integer NOT NULL,
                CONSTRAINT "PK_Owner" PRIMARY KEY ("Id")
            );
            CREATE TABLE "{_live.Name}"."Doc" (
                "Id" integer NOT NULL,
                "OwnerId" integer NOT NULL,
                "Name" text,
                "Score" integer,
                CONSTRAINT "PK_Doc" PRIMARY KEY ("Id"),
                CONSTRAINT "FK_Doc_Owner" FOREIGN KEY ("OwnerId") REFERENCES "{_live.Name}"."Owner" ("Id")
            );
            {string.Join("\n", indexStatements.Select(s => s.Replace("<s>", $"\"{_live.Name}\"")))}
            """);

    [SkippableFact]
    public async Task An_index_the_desired_state_adds_is_created()
    {
        await DeclareAsync("""CREATE INDEX "IX_Doc_OwnerId" ON <s>."Doc" ("OwnerId");""");

        var plan = await ApplyAsync();

        // Parity with the first provider, which folds an index change into the
        // table's own change rather than giving it a separate plan entry.
        Assert.Contains(plan.Actions, a => a.ObjectType == "Table" && a.ObjectName.EndsWith("Doc"));
        Assert.Contains("IX_Doc_OwnerId", await IndexDefinitionsAsync());
        await AssertConvergedAsync();
    }

    [SkippableFact]
    public async Task An_index_the_desired_state_no_longer_declares_is_dropped()
    {
        await DeclareAsync("""CREATE INDEX "IX_Doc_OwnerId" ON <s>."Doc" ("OwnerId");""");
        await ApplyAsync();

        await DeclareAsync();
        await ApplyAsync();

        Assert.DoesNotContain("IX_Doc_OwnerId", await IndexDefinitionsAsync());
        await AssertConvergedAsync();
    }

    // The aspects a per-column projection cannot see. Each is applied on top of a
    // live index that differs from the declaration only there, so a comparison
    // blind to it reports "no changes" — a silent lie about a schema that has
    // drifted, which is worse than the refusal this capability replaced.
    [SkippableTheory]
    [InlineData("""("Name")""", """("Name" DESC)""")]
    [InlineData("""("Score")""", """("Score" NULLS FIRST)""")]
    [InlineData("""("Name")""", """("Name" text_pattern_ops)""")]
    [InlineData("""("Name")""", """("Name" COLLATE "C")""")]
    [InlineData("""(lower("Name"))""", """(upper("Name"))""")]
    [InlineData("""("OwnerId")""", """("OwnerId") INCLUDE ("Name")""")]
    [InlineData("""("OwnerId") WHERE "Score" > 1""", """("OwnerId") WHERE "Score" > 2""")]
    [InlineData("""("OwnerId")""", """("OwnerId", "Score")""")]
    public async Task An_index_that_differs_only_in_its_ordering_or_shape_is_a_change(
        string live, string declared)
    {
        // The live side is seeded directly rather than applied, so what this
        // measures is the comparison alone: the index exists on both sides under
        // the same name, and the only question is whether the difference is seen.
        await PgTestSchema.ExecuteAsync($"""CREATE INDEX "IX_Doc" ON "{_live.Name}"."Doc" {live};""");
        await DeclareAsync($"""CREATE INDEX "IX_Doc" ON <s>."Doc" {declared};""");

        await ApplyAsync();

        await AssertConvergedAsync();
    }

    [SkippableFact]
    public async Task A_unique_index_and_a_plain_one_of_the_same_shape_are_different_indexes()
    {
        await DeclareAsync("""CREATE INDEX "IX_Doc_Name" ON <s>."Doc" ("Name");""");
        await ApplyAsync();

        await DeclareAsync("""CREATE UNIQUE INDEX "IX_Doc_Name" ON <s>."Doc" ("Name");""");
        await ApplyAsync();

        Assert.Contains("CREATE UNIQUE INDEX", await IndexDefinitionsAsync());
        await AssertConvergedAsync();
    }

    [SkippableFact]
    public async Task A_non_btree_method_survives_the_round_trip()
    {
        await DeclareAsync("""CREATE INDEX "IX_Doc_Score" ON <s>."Doc" USING hash ("Score");""");

        await ApplyAsync();

        Assert.Contains("USING hash", await IndexDefinitionsAsync());
        await AssertConvergedAsync();
    }

    // An index a constraint owns is the constraint's business: emitting it
    // separately would produce a desired state that cannot be applied twice.
    [SkippableFact]
    public async Task The_index_behind_a_primary_key_is_not_planned_separately()
    {
        await DeclareAsync();

        var diff = await DiffOperation.RunAsync(_provider, _ledger, _schemaDir, _url, allowDestructive: false);

        Assert.True(diff.Success, string.Join("; ", diff.Errors.Select(e => e.Text)));
        Assert.False(diff.Plan!.HasChanges);
    }

    // CONCURRENTLY exists to avoid the lock a plain CREATE INDEX takes, and it
    // buys that by refusing to run inside a transaction. This provider applies in
    // one transaction it owns — that is the atomicity it declares — so honoring
    // the keyword would mean quietly dropping either the keyword or the
    // guarantee. It refuses instead, and names which of the two it will not give up.
    [SkippableFact]
    public async Task A_concurrent_index_build_is_refused_rather_than_silently_serialized()
    {
        await DeclareAsync("""CREATE INDEX CONCURRENTLY "IX_Doc_OwnerId" ON <s>."Doc" ("OwnerId");""");

        var error = await Assert.ThrowsAsync<UnsupportedByProviderException>(
            () => DiffOperation.RunAsync(_provider, _ledger, _schemaDir, _url, allowDestructive: false));

        Assert.Contains("CONCURRENTLY", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<Schemorph.Core.Planning.Plan> ApplyAsync()
    {
        var diff = await DiffOperation.RunAsync(_provider, _ledger, _schemaDir, _url, allowDestructive: true);
        Assert.True(diff.Success, string.Join("; ", diff.Errors.Select(e => e.Text)));
        Assert.True(diff.Plan!.HasChanges, "the declaration differs from the live schema");

        var apply = await ApplyOperation.RunAsync(_provider, _ledger,
            new ApplyOperation.Request(_schemaDir, _url, ExpectedPlanHash: null, AllowDestructive: true));
        Assert.True(apply.Success, string.Join("; ", apply.Errors.Select(e => e.Text)));
        return diff.Plan;
    }

    private async Task AssertConvergedAsync()
    {
        var rediff = await DiffOperation.RunAsync(_provider, _ledger, _schemaDir, _url, allowDestructive: true);
        Assert.True(rediff.Success, string.Join("; ", rediff.Errors.Select(e => e.Text)));
        Assert.False(rediff.Plan!.HasChanges, "the plan must empty after the apply");
    }

    private async Task<string> IndexDefinitionsAsync()
    {
        await using var connection = new NpgsqlConnection(_url);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT coalesce(string_agg(indexdef, E'\\n'), '') FROM pg_indexes WHERE schemaname = @schema;",
            connection);
        command.Parameters.AddWithValue("schema", _live.Name);
        return (string)(await command.ExecuteScalarAsync())!;
    }
}
