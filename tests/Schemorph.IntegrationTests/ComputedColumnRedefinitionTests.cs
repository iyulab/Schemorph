using Schemorph.Core.Operations;
using Schemorph.Core.Planning;
using Schemorph.Provider.SqlServer;

namespace Schemorph.IntegrationTests;

/// <summary>
/// The other engine's shape of "a column dropped and re-added under the same
/// name" (docs/limitations.md § Two database engines): PostgreSQL detects it from
/// a generated-column expression change (<c>PostgresProvider.RecreatesColumn</c>);
/// on SQL Server the same edit — redefining a computed column's expression —
/// has no in-place form either, so DacFx reports it as a drop beside an add that
/// happen to share a name. Whether that is excluded from the destructive gate for
/// the stated reason (values are the new definition's output, not lost) had never
/// been measured on this engine — <see cref="RenameTests"/> covers a widened
/// column (no drop involved at all) and a true rename (gated), neither of which is
/// this shape.
/// </summary>
public sealed class ComputedColumnRedefinitionTests : IDisposable
{
    private readonly TestDatabase _db = new();
    private readonly string _dir = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), $"schemorph-recompute-{Guid.NewGuid():N}")).FullName;
    private readonly SqlServerProvider _provider = new();
    private readonly SqlServerLedgerStore _ledger = new();

    private string SchemaDir => Path.Combine(_dir, "schema");

    private void WriteDesired(string expression)
    {
        var path = Path.Combine(SchemaDir, "tables", "dbo.Lines.sql");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, $"""
            CREATE TABLE dbo.Lines (
                Id INT NOT NULL PRIMARY KEY,
                Qty INT NOT NULL,
                Price DECIMAL(18, 4) NOT NULL,
                Total AS ({expression})
            );
            GO

            """);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [SkippableFact]
    public async Task Redefining_a_computed_column_is_not_gated()
    {
        WriteDesired("Qty * Price");
        var created = await ApplyOperation.RunAsync(_provider, _ledger,
            new ApplyOperation.Request(SchemaDir, _db.Url));
        Assert.True(created.Success, string.Join("; ", created.Errors.Select(e => e.Text)));
        _db.Execute("INSERT INTO dbo.Lines (Id, Qty, Price) VALUES (1, 3, 10.00);");

        WriteDesired("Qty * Price * 2");   // same name, new formula -- no in-place ALTER exists
        var diff = await DiffOperation.RunAsync(_provider, _ledger, SchemaDir, _db.Url, allowDestructive: false);
        Assert.True(diff.Success, string.Join("; ", diff.Errors.Select(e => e.Text)));

        // The doc's claim: excluded from the gate, an ordinary alter carries it out
        // without --allow-destructive.
        var action = Assert.Single(diff.Plan!.Actions);
        Assert.Equal(RiskLevel.Warning, action.Risk);
        Assert.False(diff.Plan.HasDestructiveChanges);
        Assert.DoesNotContain(diff.Plan.Messages, m => m.Code == "SCHEMORPH001");

        var outcome = await ApplyOperation.RunAsync(_provider, _ledger,
            new ApplyOperation.Request(SchemaDir, _db.Url,
                ExpectedPlanHash: PlanFingerprint.Compute(diff.Plan)));
        Assert.True(outcome.Success, string.Join("; ", outcome.Errors.Select(e => e.Text)));

        Assert.Equal(1, _db.Scalar<int>("SELECT COUNT(*) FROM dbo.Lines"));
        Assert.Equal(60m, _db.Scalar<decimal>("SELECT Total FROM dbo.Lines WHERE Id = 1"));
    }
}
