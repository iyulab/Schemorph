using Npgsql;
using Schemorph.Core.Operations;
using Schemorph.Core.Planning;

namespace Schemorph.Provider.Postgres.Tests;

using ProgrammableFile = PgDesiredState.ProgrammableFile;

/// <summary>
/// A view's column list changing anywhere but the end used to mean <c>apply</c>
/// failed at 42P16 with a plan that had called the statement <c>risk: safe</c>.
/// These prove the fix end to end — the planner's own classification, the
/// SQLSTATE PostgreSQL really raises, and the full diff/apply round trip.
/// </summary>
public class ViewRedefineDropAndCreateTests
{
    [SkippableFact]
    public async Task Appending_a_column_stays_a_plain_redefine_and_clears_the_blanket_warning()
    {
        await using var live = await PgTestSchema.CreateAsync("""
            CREATE TABLE t (a int, b int);
            CREATE VIEW v AS SELECT a, b FROM t;
            """);
        var url = new NpgsqlConnectionStringBuilder(PgTestSchema.ServerUrl!) { SearchPath = live.Name }
            .ConnectionString;

        var analysis = PgProgrammables.Analyze(new[]
        {
            new ProgrammableFile("v.sql", "CREATE VIEW v AS SELECT a, b, 1 AS c FROM t;"),
        });
        var modelTexts = new[] { "CREATE TABLE t (a int, b int);" };

        var refined = await ViewRedefinePlanner.RefineAsync(
            analysis, modelTexts, url, live.Name, CancellationToken.None);

        var view = Assert.Single(refined.Objects);
        Assert.Null(view.RiskOverride);
        Assert.Null(view.RiskNote);
        Assert.DoesNotContain("DROP VIEW", view.ApplyScript, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, view.StatementCount);
        Assert.Empty(refined.Messages);
    }

    [SkippableFact]
    public async Task A_column_inserted_in_the_middle_gets_a_drop_and_create_plan()
    {
        // The live table deliberately does NOT yet have "c" — at `diff` time
        // nothing has been applied, so only the shadow (materialized from the
        // desired-state table file below) carries the desired shape.
        await using var live = await PgTestSchema.CreateAsync("""
            CREATE TABLE t (a int, b int, created_at timestamptz);
            CREATE VIEW v AS SELECT a, b, created_at FROM t;
            """);
        var url = new NpgsqlConnectionStringBuilder(PgTestSchema.ServerUrl!) { SearchPath = live.Name }
            .ConnectionString;

        var analysis = PgProgrammables.Analyze(new[]
        {
            new ProgrammableFile("v.sql", "CREATE VIEW v AS SELECT a, b, c, created_at FROM t;"),
        });
        var modelTexts = new[] { "CREATE TABLE t (a int, b int, c int, created_at timestamptz);" };

        var refined = await ViewRedefinePlanner.RefineAsync(
            analysis, modelTexts, url, live.Name, CancellationToken.None);

        var view = Assert.Single(refined.Objects);
        Assert.Equal(RiskLevel.Warning, view.RiskOverride);
        Assert.Contains("drops and re-creates", view.RiskNote, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("DROP VIEW", view.ApplyScript, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, view.StatementCount);
        Assert.Empty(refined.Messages);

        // Now play the declarative stage forward for real (ADR-0002 ordering:
        // it commits before redefines run) and prove both halves against the
        // live table: CREATE OR REPLACE alone really does fail 42P16 on this
        // exact shape, and the planner's own script is what actually converges.
        await using var conn2 = new NpgsqlConnection(url);
        await conn2.OpenAsync();
        await new NpgsqlCommand("ALTER TABLE t ADD COLUMN c int", conn2).ExecuteNonQueryAsync();

        var ex = await Assert.ThrowsAsync<PostgresException>(() =>
            new NpgsqlCommand("CREATE OR REPLACE VIEW v AS SELECT a, b, c, created_at FROM t;", conn2)
                .ExecuteNonQueryAsync());
        Assert.Equal("42P16", ex.SqlState);

        await new NpgsqlCommand(view.ApplyScript, conn2).ExecuteNonQueryAsync();
    }

    [SkippableFact]
    public async Task A_dependent_view_is_refused_rather_than_dropped_blindly()
    {
        await using var live = await PgTestSchema.CreateAsync("""
            CREATE TABLE t (a int, b int, created_at timestamptz);
            CREATE VIEW v AS SELECT a, b, created_at FROM t;
            CREATE VIEW v2 AS SELECT * FROM v;
            """);
        var url = new NpgsqlConnectionStringBuilder(PgTestSchema.ServerUrl!) { SearchPath = live.Name }
            .ConnectionString;

        var analysis = PgProgrammables.Analyze(new[]
        {
            new ProgrammableFile("v.sql", "CREATE VIEW v AS SELECT a, b, c, created_at FROM t;"),
        });
        var modelTexts = new[] { "CREATE TABLE t (a int, b int, c int, created_at timestamptz);" };

        var refined = await ViewRedefinePlanner.RefineAsync(
            analysis, modelTexts, url, live.Name, CancellationToken.None);

        var error = Assert.Single(refined.Messages);
        Assert.Equal("Error", error.Severity);
        Assert.Equal("SCHEMORPH010", error.Code);
        Assert.Contains("v:", error.Text);
        Assert.Contains("CASCADE", error.Text, StringComparison.OrdinalIgnoreCase);

        // Refused, not silently guessed — the object's own plan action is
        // untouched (still the ordinary CREATE OR REPLACE it started as).
        var view = Assert.Single(refined.Objects);
        Assert.DoesNotContain("DROP VIEW", view.ApplyScript, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The probe runs the file's query inside the shadow schema, so every
    /// reference to the source schema in the body must be retargeted there —
    /// <c>FROM public.t</c> names its schema explicitly and would otherwise
    /// resolve past the shadow to the live table. No database needed: this is
    /// the rewritten text itself.
    /// </summary>
    [Fact]
    public void The_probe_retargets_schema_qualified_body_references_into_the_shadow()
    {
        const string script = """
            CREATE OR REPLACE VIEW public.v AS
            SELECT b.a, b.c, u.name, (SELECT count(*) FROM public.w WHERE w.b_id = b.a) AS n
            FROM public.t AS b
            LEFT JOIN public.u AS u ON u.id = b.u_id
            LEFT JOIN other.x AS x ON x.id = b.x_id;
            """;

        var probe = ViewRedefinePlanner.RetargetForProbe(script, "probe_1", "public", "shadow_x");

        Assert.Contains("shadow_x.probe_1", probe);
        Assert.Matches(@"FROM shadow_x\.t\s+(AS\s+)?b\b", probe);
        Assert.Matches(@"JOIN shadow_x\.u\s+(AS\s+)?u\b", probe);
        Assert.Contains("FROM shadow_x.w", probe);
        Assert.Matches(@"JOIN other\.x\s+(AS\s+)?x\b", probe);   // another schema is a real cross-schema reference
        Assert.DoesNotContain("public.", probe);
        Assert.DoesNotContain("OR REPLACE", probe, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_probe_lands_in_the_shadow_even_when_the_file_left_its_target_unqualified()
    {
        var probe = ViewRedefinePlanner.RetargetForProbe(
            "CREATE OR REPLACE VIEW v AS SELECT a FROM t;", "probe_1", "public", "shadow_x");

        Assert.Contains("shadow_x.probe_1", probe);
        Assert.Contains("shadow_x.t", probe);
    }

    /// <summary>
    /// The shape generated desired-state files usually take: the body names its
    /// schema explicitly, and the table changing in the same plan is the one
    /// the view selects from. At <c>diff</c> time the live table lacks the new
    /// column, so only a probe that stays inside the shadow can succeed.
    /// </summary>
    [SkippableFact]
    public async Task A_schema_qualified_body_is_probed_inside_the_shadow_not_against_live()
    {
        await using var live = await PgTestSchema.CreateAsync("""
            CREATE TABLE t (a int, b int, created_at timestamptz);
            CREATE VIEW v AS SELECT b.a, b.b, b.created_at FROM t AS b;
            """);
        var url = new NpgsqlConnectionStringBuilder(PgTestSchema.ServerUrl!) { SearchPath = live.Name }
            .ConnectionString;
        var q = live.Name;

        var analysis = PgProgrammables.Analyze(new[]
        {
            new ProgrammableFile("v.sql",
                $"CREATE VIEW {q}.v AS SELECT b.a, b.b, b.c, b.created_at FROM {q}.t AS b;"),
        });
        var modelTexts = new[] { $"CREATE TABLE {q}.t (a int, b int, c int, created_at timestamptz);" };

        var refined = await ViewRedefinePlanner.RefineAsync(
            analysis, modelTexts, url, live.Name, CancellationToken.None);

        var view = Assert.Single(refined.Objects);
        Assert.Empty(refined.Messages);
        Assert.StartsWith("DROP VIEW", view.ApplyScript, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, view.StatementCount);
    }

    [SkippableFact]
    public async Task A_view_that_does_not_exist_live_yet_is_a_plain_create_without_the_blanket_warning()
    {
        await using var live = await PgTestSchema.CreateAsync("CREATE TABLE t (a int, b int);");
        var url = new NpgsqlConnectionStringBuilder(PgTestSchema.ServerUrl!) { SearchPath = live.Name }
            .ConnectionString;

        var analysis = PgProgrammables.Analyze(new[]
        {
            new ProgrammableFile("v.sql", "CREATE VIEW v AS SELECT a, b FROM t;"),
        });

        var refined = await ViewRedefinePlanner.RefineAsync(
            analysis, new[] { "CREATE TABLE t (a int, b int);" }, url, live.Name, CancellationToken.None);

        var view = Assert.Single(refined.Objects);
        Assert.Null(view.RiskOverride);
        Assert.Null(view.RiskNote);
        Assert.Equal(1, view.StatementCount);
    }

    [SkippableFact]
    public async Task Diff_and_apply_converge_when_table_and_schema_qualified_view_change_in_one_plan()
    {
        await using var live = await PgTestSchema.CreateAsync("""
            CREATE TABLE t (a int, b int, created_at timestamptz);
            CREATE VIEW v AS SELECT b.a, b.b, b.created_at FROM t AS b;
            """);
        var url = new NpgsqlConnectionStringBuilder(PgTestSchema.ServerUrl!) { SearchPath = live.Name }
            .ConnectionString;
        var q = live.Name;
        var provider = new PostgresProvider();
        var ledger = new PostgresLedgerStore();

        var schemaDir = Path.Combine(
            Path.GetTempPath(), "schemorph-pg-view-qualified-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(Path.Combine(schemaDir, "tables"));
        Directory.CreateDirectory(Path.Combine(schemaDir, "views"));
        try
        {
            await File.WriteAllTextAsync(Path.Combine(schemaDir, "tables", "t.sql"),
                $"CREATE TABLE {q}.t (a int, b int, c int, created_at timestamptz);");
            await File.WriteAllTextAsync(Path.Combine(schemaDir, "views", "v.sql"),
                $"CREATE VIEW {q}.v AS SELECT b.a, b.b, b.c, b.created_at FROM {q}.t AS b;");

            var diff = await DiffOperation.RunAsync(provider, ledger, schemaDir, url, allowDestructive: false);
            Assert.True(diff.Success, string.Join("; ", diff.Errors.Select(e => e.Text)));

            var viewAction = Assert.Single(diff.Plan!.Actions, a => a.ObjectName == "v");
            Assert.Equal(2, viewAction.StatementCount);
            Assert.StartsWith("DROP VIEW", viewAction.Sql, StringComparison.OrdinalIgnoreCase);

            var apply = await ApplyOperation.RunAsync(provider, ledger, new ApplyOperation.Request(schemaDir, url));
            Assert.True(apply.Success, string.Join("; ", apply.Errors.Select(e => e.Text)));

            var rediff = await DiffOperation.RunAsync(provider, ledger, schemaDir, url, allowDestructive: false);
            Assert.True(rediff.Success, string.Join("; ", rediff.Errors.Select(e => e.Text)));
            Assert.False(rediff.Plan!.HasChanges);
        }
        finally
        {
            try { Directory.Delete(schemaDir, recursive: true); } catch { }
        }
    }

    [SkippableFact]
    public async Task Diff_and_apply_converge_on_a_mid_list_column_insertion()
    {
        await using var live = await PgTestSchema.CreateAsync("""
            CREATE TABLE t (a int, b int, created_at timestamptz);
            CREATE VIEW v AS SELECT a, b, created_at FROM t;
            """);
        var url = new NpgsqlConnectionStringBuilder(PgTestSchema.ServerUrl!) { SearchPath = live.Name }
            .ConnectionString;
        var provider = new PostgresProvider();
        var ledger = new PostgresLedgerStore();

        var schemaDir = Path.Combine(
            Path.GetTempPath(), "schemorph-pg-view-dropcreate-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(Path.Combine(schemaDir, "tables"));
        Directory.CreateDirectory(Path.Combine(schemaDir, "views"));
        try
        {
            await File.WriteAllTextAsync(Path.Combine(schemaDir, "tables", "t.sql"),
                "CREATE TABLE t (a int, b int, c int, created_at timestamptz);");
            await File.WriteAllTextAsync(Path.Combine(schemaDir, "views", "v.sql"),
                "CREATE VIEW v AS SELECT a, b, c, created_at FROM t;");

            var diff = await DiffOperation.RunAsync(provider, ledger, schemaDir, url, allowDestructive: false);
            Assert.True(diff.Success, string.Join("; ", diff.Errors.Select(e => e.Text)));

            var viewAction = Assert.Single(diff.Plan!.Actions, a => a.ObjectName == "v");
            Assert.Equal(2, viewAction.StatementCount);
            Assert.StartsWith("DROP VIEW", viewAction.Sql, StringComparison.OrdinalIgnoreCase);

            var apply = await ApplyOperation.RunAsync(provider, ledger, new ApplyOperation.Request(schemaDir, url));
            Assert.True(apply.Success, string.Join("; ", apply.Errors.Select(e => e.Text)));

            var rediff = await DiffOperation.RunAsync(provider, ledger, schemaDir, url, allowDestructive: false);
            Assert.True(rediff.Success, string.Join("; ", rediff.Errors.Select(e => e.Text)));
            Assert.False(rediff.Plan!.HasChanges);
        }
        finally
        {
            try { Directory.Delete(schemaDir, recursive: true); } catch { }
        }
    }
}
