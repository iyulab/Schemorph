using Npgsql;
using Schemorph.Core.Operations;

namespace Schemorph.Provider.Postgres.Tests;

/// <summary>
/// End-to-end regression for ROADMAP §3 (cycle-122): a real <c>PostgresException</c> from a
/// live apply, not just <see cref="PgSqlStateHints"/>'s lookup table in isolation — proving the
/// hint actually reaches <see cref="Schemorph.Core.Providers.ApplyResult"/> through
/// <see cref="PostgresProvider.ApplyAsync"/>'s catch block, the same path the CLI and MCP
/// surfaces both read <c>RawMessage.Text</c> from.
/// </summary>
public sealed class PgSqlStateHintApplyTests : IAsyncLifetime
{
    private PgTestSchema _live = null!;
    private string _url = null!;
    private string _schemaDir = null!;
    private readonly PostgresProvider _provider = new();
    private readonly PostgresLedgerStore _ledger = new();

    // Two rows sharing "Name" — the duplicate a UNIQUE index declared in the desired state
    // below will reject with SQLSTATE 23505 once applied.
    private const string LiveV1 = """
        CREATE TABLE "Doc" (
            "Id" integer NOT NULL,
            "Name" text,
            CONSTRAINT "PK_Doc" PRIMARY KEY ("Id")
        );
        INSERT INTO "Doc" ("Id", "Name") VALUES (1, 'dup'), (2, 'dup');
        """;

    public async Task InitializeAsync()
    {
        _live = await PgTestSchema.CreateAsync(LiveV1);
        _url = new NpgsqlConnectionStringBuilder(PgTestSchema.ServerUrl!) { SearchPath = _live.Name }
            .ConnectionString;
        _schemaDir = Path.Combine(
            Path.GetTempPath(), "schemorph-pg-sqlstate-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(Path.Combine(_schemaDir, "tables"));
        await File.WriteAllTextAsync(Path.Combine(_schemaDir, "tables", "schema.sql"), $"""
            CREATE TABLE "{_live.Name}"."Doc" (
                "Id" integer NOT NULL,
                "Name" text,
                CONSTRAINT "PK_Doc" PRIMARY KEY ("Id")
            );
            CREATE UNIQUE INDEX "IX_Doc_Name" ON "{_live.Name}"."Doc" ("Name");
            """);
    }

    public async Task DisposeAsync()
    {
        await _live.DisposeAsync();
        try { Directory.Delete(_schemaDir, recursive: true); } catch { }
    }

    [SkippableFact]
    public async Task Unique_violation_against_existing_duplicate_data_carries_the_curated_hint()
    {
        Skip.If(PgTestSchema.ServerUrl is null, "SCHEMORPH_PG_TEST_URL is not set; Postgres tests need a live server.");

        var outcome = await ApplyOperation.RunAsync(
            _provider, _ledger, new ApplyOperation.Request(_schemaDir, _url));

        Assert.False(outcome.Success);
        var message = Assert.Single(outcome.Errors);
        Assert.Equal("23505", message.Code);
        // The engine's own wording for this specific trigger (CREATE UNIQUE INDEX against
        // pre-existing duplicates) — distinct from the "duplicate key value violates unique
        // constraint" wording an INSERT/UPDATE conflict gets under the same SQLSTATE.
        Assert.Contains("could not create unique index", message.Text);
        Assert.Contains("de-duplicate the data first", message.Text);
    }
}
