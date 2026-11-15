using Schemorph.Core.Operations;
using Schemorph.Provider.SqlServer;

namespace Schemorph.IntegrationTests;

/// <summary>
/// Two tables that point at each other. On Postgres the shadow had to learn to add the key that closes the cycle after
/// both tables exist; this is the same desired state on SQL Server, where the comparison is built by the engine's own
/// deployment model — measured here rather than assumed to behave.
/// </summary>
public sealed class CircularForeignKeyTests : IDisposable
{
    private readonly TestDatabase _db = new();
    private readonly string _dir = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), $"schemorph-cycle-{Guid.NewGuid():N}")).FullName;
    private readonly SqlServerProvider _provider = new();
    private readonly SqlServerLedgerStore _ledger = new();

    private string SchemaDir => Path.Combine(_dir, "schema");

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private void Write(string table, string body)
    {
        var path = Path.Combine(SchemaDir, "tables", $"dbo.{table}.sql");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, body);
    }

    [SkippableFact]
    public async Task Tables_that_reference_each_other_apply_and_the_next_run_has_nothing_to_do()
    {
        Write("Dept", """
            CREATE TABLE dbo.[Dept] (
                Id INT NOT NULL PRIMARY KEY,
                HeadId INT NULL,
                CONSTRAINT FK_Dept_Emp FOREIGN KEY (HeadId) REFERENCES dbo.[Emp] (Id)
            );
            GO

            """);
        Write("Emp", """
            CREATE TABLE dbo.[Emp] (
                Id INT NOT NULL PRIMARY KEY,
                DeptId INT NULL,
                CONSTRAINT FK_Emp_Dept FOREIGN KEY (DeptId) REFERENCES dbo.[Dept] (Id)
            );
            GO

            """);

        var first = await ApplyOperation.RunAsync(_provider, _ledger, new ApplyOperation.Request(SchemaDir, _db.Url));

        Assert.True(first.Success, string.Join("; ", first.Errors.Select(e => e.Text)));
        Assert.Equal(2, _db.Scalar<int>("SELECT COUNT(*) FROM sys.foreign_keys WHERE name IN ('FK_Dept_Emp', 'FK_Emp_Dept')"));

        var second = await ApplyOperation.RunAsync(_provider, _ledger, new ApplyOperation.Request(SchemaDir, _db.Url));
        Assert.True(second.Success, string.Join("; ", second.Errors.Select(e => e.Text)));
        Assert.Empty(second.Applied);
    }
}
