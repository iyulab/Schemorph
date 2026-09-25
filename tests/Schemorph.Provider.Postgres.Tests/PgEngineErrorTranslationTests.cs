using Npgsql;
using Schemorph.Core.Errors;
using Schemorph.Core.Operations;
using Schemorph.Core.Providers;

namespace Schemorph.Provider.Postgres.Tests;

/// <summary>
/// An engine error reads the same whichever stage raised it: the SQLSTATE, the engine's
/// primary message, and either the curated hint or the untranslated marker — never the bare
/// exception text, which appends a statement offset into SQL the reader never sees. Each test
/// drives a different boundary against a live server: the declarative publish, a migration,
/// and a comparison that throws.
/// </summary>
public sealed class PgEngineErrorTranslationTests : IAsyncLifetime
{
    private PgTestSchema _live = null!;
    private PgTestSchema? _other;
    private string _url = null!;
    private string _schemaDir = null!;
    private readonly PostgresProvider _provider = new();
    private readonly PostgresLedgerStore _ledger = new();

    private const string LiveV1 = """
        CREATE TABLE "Doc" ("Id" integer NOT NULL, "Name" text, CONSTRAINT "PK_Doc" PRIMARY KEY ("Id"));
        """;

    public async Task InitializeAsync()
    {
        _live = await PgTestSchema.CreateAsync(LiveV1);
        _url = new NpgsqlConnectionStringBuilder(PgTestSchema.ServerUrl!) { SearchPath = _live.Name }
            .ConnectionString;
        _schemaDir = Path.Combine(
            Path.GetTempPath(), "schemorph-pg-engine-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(Path.Combine(_schemaDir, "tables"));
    }

    public async Task DisposeAsync()
    {
        if (_other is not null) await _other.DisposeAsync();
        await _live.DisposeAsync();
        try { Directory.Delete(_schemaDir, recursive: true); } catch { }
    }

    private Task WriteTableAsync(string columns) => File.WriteAllTextAsync(
        Path.Combine(_schemaDir, "tables", "Doc.sql"),
        $"""CREATE TABLE "{_live.Name}"."Doc" ({columns}, CONSTRAINT "PK_Doc" PRIMARY KEY ("Id"));""");

    [SkippableFact]
    public async Task A_drop_blocked_by_a_dependent_outside_the_desired_state_is_translated_at_publish()
    {
        Skip.If(PgTestSchema.ServerUrl is null, "SCHEMORPH_PG_TEST_URL is not set; Postgres tests need a live server.");

        // A view in another schema reads the column — outside the desired state and outside
        // the target schema, so nothing in the plan removes it and the engine refuses the drop.
        _other = await PgTestSchema.CreateAsync(
            $"""CREATE VIEW "DocNames" AS SELECT "Name" FROM "{_live.Name}"."Doc";""");
        await WriteTableAsync("\"Id\" integer NOT NULL");

        var outcome = await ApplyOperation.RunAsync(
            _provider, _ledger, new ApplyOperation.Request(_schemaDir, _url, AllowDestructive: true));

        Assert.False(outcome.Success);
        Assert.Equal(ApplyOperation.FailureStage.Publish, outcome.Stage);
        Assert.Equal(new EngineErrorInfo("2BP01", Translated: true), outcome.Engine);
        var message = Assert.Single(outcome.Errors);
        Assert.Equal("2BP01", message.Code);
        Assert.Contains("because other objects depend on it", message.Text);
        Assert.Contains("still depends on what this statement drops", message.Text);
    }

    [SkippableFact]
    public async Task A_code_with_no_translation_is_marked_untranslated_in_the_migration_stage()
    {
        Skip.If(PgTestSchema.ServerUrl is null, "SCHEMORPH_PG_TEST_URL is not set; Postgres tests need a live server.");

        await WriteTableAsync("\"Id\" integer NOT NULL, \"Name\" text");
        var migrations = Path.Combine(_schemaDir, "..", Path.GetFileName(_schemaDir) + "-migrations");
        Directory.CreateDirectory(migrations);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(migrations, "V0001__divide.sql"), "SELECT 1/0;");

            var outcome = await ApplyOperation.RunAsync(
                _provider, _ledger, new ApplyOperation.Request(_schemaDir, _url, MigrationsDir: migrations));

            Assert.False(outcome.Success);
            Assert.Equal(ApplyOperation.FailureStage.Migration, outcome.Stage);
            // 22012 (division_by_zero) is deliberately not curated: it is the database's
            // message, and saying so is the whole of the honest description.
            Assert.Equal(new EngineErrorInfo("22012", Translated: false), outcome.Engine);
            var text = Assert.Single(outcome.Errors).Text;
            Assert.Contains("22012: division by zero", text);
            Assert.Contains(EngineError.UntranslatedMarker, text);
        }
        finally
        {
            Directory.Delete(migrations, recursive: true);
        }
    }

    [SkippableFact]
    public async Task A_comparison_that_throws_is_described_without_the_statement_offset()
    {
        Skip.If(PgTestSchema.ServerUrl is null, "SCHEMORPH_PG_TEST_URL is not set; Postgres tests need a live server.");

        // The desired state contradicts itself: the view reads a column its table no longer
        // declares. The view exists live, so the comparison probes the file's query against
        // the desired table shape in its shadow schema, and the engine rejects it there.
        await PgTestSchema.ExecuteAsync(
            $"""CREATE VIEW "{_live.Name}"."DocNames" AS SELECT "Name" FROM "{_live.Name}"."Doc";""");
        await WriteTableAsync("\"Id\" integer NOT NULL");
        Directory.CreateDirectory(Path.Combine(_schemaDir, "views"));
        await File.WriteAllTextAsync(Path.Combine(_schemaDir, "views", "DocNames.sql"),
            """CREATE VIEW "DocNames" AS SELECT "Name" FROM "Doc";""");

        var thrown = await Record.ExceptionAsync(() =>
            DiffOperation.RunAsync(_provider, _ledger, _schemaDir, _url, allowDestructive: false));

        Assert.NotNull(thrown);
        var error = SchemorphError.ForUnclassified("compare_failed", thrown, _provider);
        Assert.Equal(new EngineErrorInfo("42703", Translated: true), error.Engine);
        Assert.StartsWith("42703: column \"Name\" does not exist", error.Message);
        Assert.DoesNotContain("POSITION", error.Message);
        Assert.Contains("References a column that does not exist", error.Hint);
    }
}
