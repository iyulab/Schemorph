using Schemorph.Core;
using Schemorph.Core.Ledger;
using Schemorph.Core.Providers;
using Schemorph.Core.Redefine;

namespace Schemorph.Core.Tests.Redefine;

public sealed class RedefineRunnerTests : IDisposable
{
    private readonly string _dir = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), $"schemorph-redef-{Guid.NewGuid():N}")).FullName;
    private readonly FakeLedger _ledger = new();
    private readonly FakeProvider _provider;

    public RedefineRunnerTests() => _provider = new FakeProvider { Ledger = _ledger };

    private RedefineRunner Runner => new(_provider, _ledger);

    private ProgrammableObjectInfo Obj(string name, string type, string body, params string[] dependsOn)
    {
        var path = Path.Combine(_dir, $"{name}.sql");
        File.WriteAllText(path, body);
        return new ProgrammableObjectInfo(name, type, path, $"CREATE OR ALTER -- {body}", dependsOn);
    }

    private static ProgrammableAnalysis Analysis(params ProgrammableObjectInfo[] objects) =>
        new(objects, Array.Empty<RawMessage>());

    [Fact]
    public async Task Plans_new_objects_in_dependency_order_not_alphabetical()
    {
        // dbo.AAA depends on dbo.ZZZ — alphabetical order would apply AAA first and fail.
        var analysis = Analysis(
            Obj("dbo.AAA", "View", "SELECT * FROM dbo.ZZZ", "dbo.ZZZ"),
            Obj("dbo.ZZZ", "View", "SELECT 1 AS One"));

        var pending = await Runner.PlanAsync(analysis, "conn");

        Assert.Equal(new[] { "dbo.ZZZ", "dbo.AAA" }, pending.Select(p => p.ObjectName));
    }

    [Fact]
    public async Task Unchanged_objects_are_skipped_changed_are_pending()
    {
        var unchanged = Obj("dbo.Same", "Procedure", "BODY A");
        var changed = Obj("dbo.Edited", "Procedure", "BODY B v2");
        _ledger.Entries.Add(new LedgerEntry(RedefineRunner.LedgerKind, "dbo.Same", "Redefine",
            ContentChecksum.Compute("BODY A"), true, null));
        _ledger.Entries.Add(new LedgerEntry(RedefineRunner.LedgerKind, "dbo.Edited", "Redefine",
            ContentChecksum.Compute("BODY B v1"), true, null));

        var pending = await Runner.PlanAsync(Analysis(unchanged, changed), "conn");

        Assert.Equal(new[] { "dbo.Edited" }, pending.Select(p => p.ObjectName));
    }

    [Fact]
    public async Task Latest_ledger_entry_wins_dropped_then_readded_object_is_pending()
    {
        // History: redefined with this exact content, then dropped (declarative path
        // recorded a tombstone). Re-adding the same file must re-create the object.
        var obj = Obj("dbo.Back", "Procedure", "BODY");
        _ledger.Entries.Add(new LedgerEntry(RedefineRunner.LedgerKind, "dbo.Back", "Redefine",
            ContentChecksum.Compute("BODY"), true, null));
        _ledger.Entries.Add(new LedgerEntry(RedefineRunner.LedgerKind, "dbo.Back", "Drop",
            Checksum: null, true, null));

        var pending = await Runner.PlanAsync(Analysis(obj), "conn");

        Assert.Equal(new[] { "dbo.Back" }, pending.Select(p => p.ObjectName));
    }

    [Fact]
    public async Task Dependency_cycle_is_rejected()
    {
        var analysis = Analysis(
            Obj("dbo.A", "View", "SELECT * FROM dbo.B", "dbo.B"),
            Obj("dbo.B", "View", "SELECT * FROM dbo.A", "dbo.A"));

        var ex = await Assert.ThrowsAsync<RedefineException>(() => Runner.PlanAsync(analysis, "conn"));

        Assert.Contains("dbo.A", ex.Message);
        Assert.Contains("dbo.B", ex.Message);
    }

    [Fact]
    public async Task Unknown_dependencies_outside_the_programmable_set_are_ignored()
    {
        // A view depending on a table: the table is not in the programmable set and
        // must not block ordering.
        var analysis = Analysis(Obj("dbo.V", "View", "SELECT * FROM dbo.SomeTable", "dbo.SomeTable"));

        var pending = await Runner.PlanAsync(analysis, "conn");

        Assert.Equal(new[] { "dbo.V" }, pending.Select(p => p.ObjectName));
    }

    [Fact]
    public async Task Run_executes_pending_apply_scripts_in_order_and_records_ledger()
    {
        var analysis = Analysis(
            Obj("dbo.AAA", "View", "SELECT * FROM dbo.ZZZ", "dbo.ZZZ"),
            Obj("dbo.ZZZ", "View", "SELECT 1 AS One"));

        var result = await Runner.RunAsync(analysis, "conn");

        Assert.Equal(new[] { "dbo.ZZZ", "dbo.AAA" }, result.Redefined);
        Assert.Equal(0, result.Skipped);
        Assert.Equal(new[]
        {
            "CREATE OR ALTER -- SELECT 1 AS One",
            "CREATE OR ALTER -- SELECT * FROM dbo.ZZZ",
        }, _provider.ExecutedScripts);
        Assert.All(_ledger.Entries, e =>
        {
            Assert.Equal(RedefineRunner.LedgerKind, e.Kind);
            Assert.Equal("Redefine", e.Operation);
            Assert.NotNull(e.Checksum);
            Assert.True(e.Succeeded);
        });
    }

    [Fact]
    public async Task Run_skips_unchanged_objects_entirely()
    {
        var obj = Obj("dbo.Same", "Procedure", "BODY");
        _ledger.Entries.Add(new LedgerEntry(RedefineRunner.LedgerKind, "dbo.Same", "Redefine",
            ContentChecksum.Compute("BODY"), true, null));

        var result = await Runner.RunAsync(Analysis(obj), "conn");

        Assert.Empty(result.Redefined);
        Assert.Equal(1, result.Skipped);
        Assert.Empty(_provider.ExecutedScripts);
    }

    [Fact]
    public async Task Recorded_checksum_matches_the_file_content()
    {
        var obj = Obj("dbo.P", "Procedure", "BODY");

        await Runner.RunAsync(Analysis(obj), "conn");

        var entry = Assert.Single(_ledger.Entries);
        Assert.Equal(ContentChecksum.Compute("BODY"), entry.Checksum);
    }

    [Fact]
    public async Task Redefine_commits_its_ledger_entry_with_the_script()
    {
        // ADR-0004 decision 2: the redefine row commits in the script's transaction.
        var obj = Obj("dbo.P", "Procedure", "BODY");

        await Runner.RunAsync(Analysis(obj), "conn");

        var (script, entries) = Assert.Single(_provider.AtomicExecutions);
        Assert.Equal("CREATE OR ALTER -- BODY", script);
        var entry = Assert.Single(entries);
        Assert.Equal("dbo.P", entry.ObjectName);
        Assert.True(entry.Succeeded);
    }

    [Fact]
    public async Task Failed_redefine_records_a_failure_row_and_stops()
    {
        var analysis = Analysis(
            Obj("dbo.AAA", "View", "SELECT * FROM dbo.ZZZ", "dbo.ZZZ"),
            Obj("dbo.ZZZ", "View", "FAILING"));
        var provider = new FakeProvider { Ledger = _ledger, FailOnScriptContaining = "FAILING" };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => new RedefineRunner(provider, _ledger).RunAsync(analysis, "conn"));

        Assert.Empty(provider.ExecutedScripts);   // dbo.ZZZ failed first; dbo.AAA never ran
        var failure = Assert.Single(_ledger.Entries, e => !e.Succeeded);
        Assert.Equal("dbo.ZZZ", failure.ObjectName);
        Assert.Contains("boom", failure.Detail);
    }

    [Fact]
    public async Task RecordDrops_appends_tombstones_for_programmable_drops_only()
    {
        await Runner.RecordDropsAsync("conn", new[]
        {
            new RawChange("Delete", "Procedure", "dbo.Gone"),
            new RawChange("Delete", "Table", "dbo.Data"),
            new RawChange("Add", "View", "dbo.New"),
        });

        var entry = Assert.Single(_ledger.Entries);
        Assert.Equal(RedefineRunner.LedgerKind, entry.Kind);
        Assert.Equal("dbo.Gone", entry.ObjectName);
        Assert.Equal("Drop", entry.Operation);
        Assert.Null(entry.Checksum);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);
}
