using Schemorph.Core.Operations;
using Schemorph.Core.Providers;
using Schemorph.Provider.SqlServer;

namespace Schemorph.IntegrationTests;

/// <summary>
/// Convergence: apply a desired state, then diff the same files again — the plan
/// must be empty. This is a provider-independent invariant (the engine may model
/// schemas however it likes; a second diff of an unchanged desired state has
/// nothing to say), and it is the one this tool category chronically breaks:
/// engines re-emit expressions from the catalog in their own normalized form
/// (parenthesization, casing, schema-qualification), so a CHECK constraint or a
/// column default that round-trips imperfectly re-diffs forever — a plan that is
/// never empty and an apply that is never done.
///
/// <see cref="CoreLoopTests"/> already pins the trivial case (a two-column table).
/// What is pinned here is the expression-bearing surface, one scenario per shape,
/// driven through the same core operations the CLI and MCP render — so the
/// invariant is asserted on the composed loop (declarative publish + programmable
/// re-definition + ledger), not on a raw provider comparison.
/// </summary>
public sealed class ConvergenceTests : IDisposable
{
    private readonly TestDatabase _db = new();
    private readonly string _dir = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), $"schemorph-conv-{Guid.NewGuid():N}")).FullName;
    private readonly SqlServerProvider _provider = new();
    private readonly SqlServerLedgerStore _ledger = new();

    /// <summary>
    /// Scenario → desired-state files. Each shape is one that a normalizing
    /// engine can plausibly fail to round-trip; the value is the whole point of
    /// the case, so it is written literally rather than generated.
    /// </summary>
    private static readonly Dictionary<string, (string Path, string Sql)[]> Scenarios = new()
    {
        ["literal-defaults"] =
        [
            ("tables/dbo.Items.sql", """
                CREATE TABLE dbo.Items (
                    Id INT NOT NULL PRIMARY KEY,
                    Qty INT NOT NULL CONSTRAINT DF_Items_Qty DEFAULT (0),
                    Note NVARCHAR(50) NOT NULL CONSTRAINT DF_Items_Note DEFAULT (''),
                    Rate DECIMAL(18, 4) NOT NULL CONSTRAINT DF_Items_Rate DEFAULT (1.5),
                    Active BIT NOT NULL CONSTRAINT DF_Items_Active DEFAULT (1)
                );
                """),
        ],
        ["function-defaults"] =
        [
            ("tables/dbo.Events.sql", """
                CREATE TABLE dbo.Events (
                    Id UNIQUEIDENTIFIER NOT NULL CONSTRAINT DF_Events_Id DEFAULT (NEWID()) PRIMARY KEY,
                    CreatedAt DATETIME2 NOT NULL CONSTRAINT DF_Events_CreatedAt DEFAULT (SYSUTCDATETIME()),
                    CreatedOffset DATETIMEOFFSET NOT NULL CONSTRAINT DF_Events_Offset DEFAULT (SYSDATETIMEOFFSET())
                );
                """),
        ],
        // `IN (...)` is deliberately absent here — it does NOT converge; see
        // Check_constraints_written_as_IN_do_not_converge below.
        ["check-constraints"] =
        [
            ("tables/dbo.Orders.sql", """
                CREATE TABLE dbo.Orders (
                    Id INT NOT NULL PRIMARY KEY,
                    Qty INT NOT NULL CONSTRAINT CK_Orders_Qty CHECK (Qty > 0),
                    Discount DECIMAL(5, 2) NULL CONSTRAINT CK_Orders_Discount CHECK (Discount >= 0 AND Discount <= 100),
                    Status CHAR(1) NOT NULL CONSTRAINT CK_Orders_Status CHECK ([Status] = 'C' OR [Status] = 'B' OR [Status] = 'A')
                );
                """),
        ],
        ["computed-columns"] =
        [
            ("tables/dbo.Lines.sql", """
                CREATE TABLE dbo.Lines (
                    Id INT NOT NULL PRIMARY KEY,
                    Qty INT NOT NULL,
                    Price DECIMAL(18, 4) NOT NULL,
                    Total AS (Qty * Price),
                    TotalPersisted AS (Qty * Price) PERSISTED
                );
                """),
        ],
        ["filtered-index"] =
        [
            ("tables/dbo.Accounts.sql", """
                CREATE TABLE dbo.Accounts (
                    Id INT NOT NULL PRIMARY KEY,
                    Email NVARCHAR(200) NOT NULL,
                    IsActive BIT NOT NULL
                );
                GO
                CREATE UNIQUE INDEX IX_Accounts_Email_Active
                    ON dbo.Accounts (Email) WHERE IsActive = 1;
                """),
        ],
        ["keys-and-references"] =
        [
            ("tables/dbo.Parent.sql", """
                CREATE TABLE dbo.Parent (
                    Id INT NOT NULL CONSTRAINT PK_Parent PRIMARY KEY,
                    Code NVARCHAR(20) NOT NULL CONSTRAINT UQ_Parent_Code UNIQUE
                );
                """),
            ("tables/dbo.Child.sql", """
                CREATE TABLE dbo.Child (
                    Id INT NOT NULL IDENTITY(1, 1) CONSTRAINT PK_Child PRIMARY KEY,
                    ParentId INT NOT NULL CONSTRAINT FK_Child_Parent REFERENCES dbo.Parent (Id) ON DELETE CASCADE,
                    Payload NVARCHAR(MAX) NULL
                );
                """),
        ],
        // Namespaces and sequences: both are objects in their own right whose
        // definitions carry values a normalizing engine can re-emit differently
        // (a sequence's START/INCREMENT/type), and both are named in the
        // Postgres provider's parity requirements — so SQL Server pins them first.
        ["schemas-and-sequences"] =
        [
            ("schemas/ops.sql", "CREATE SCHEMA ops;"),
            ("sequences/ops.OrderNumber.sql", """
                CREATE SEQUENCE ops.OrderNumber
                    AS BIGINT
                    START WITH 1000
                    INCREMENT BY 1
                    MINVALUE 1000
                    NO MAXVALUE
                    NO CYCLE;
                """),
            ("tables/ops.Orders.sql", """
                CREATE TABLE ops.Orders (
                    Id INT NOT NULL PRIMARY KEY,
                    Number BIGINT NOT NULL CONSTRAINT DF_Orders_Number DEFAULT (NEXT VALUE FOR ops.OrderNumber)
                );
                """),
        ],
        // Strategy 2 (ADR-0002): the view never goes through the declarative diff —
        // its convergence is the redefine checksum's, recorded in the ledger by the
        // apply. A second diff must show neither a declarative change nor a pending
        // re-definition.
        ["programmable-objects"] =
        [
            ("tables/dbo.Sales.sql", """
                CREATE TABLE dbo.Sales (
                    Id INT NOT NULL PRIMARY KEY,
                    Amount DECIMAL(18, 2) NOT NULL
                );
                """),
            ("views/dbo.SalesSummary.sql", """
                CREATE VIEW dbo.SalesSummary
                AS
                    SELECT COUNT(*) AS Sales, SUM(s.Amount) AS Total FROM dbo.Sales AS s;
                """),
            ("procedures/dbo.GetSales.sql", """
                CREATE PROCEDURE dbo.GetSales
                    @MinAmount DECIMAL(18, 2) = 0
                AS
                    SELECT s.Id, s.Amount FROM dbo.Sales AS s WHERE s.Amount >= @MinAmount;
                """),
        ],
    };

    public static TheoryData<string> Cases()
    {
        var data = new TheoryData<string>();
        foreach (var name in Scenarios.Keys.OrderBy(n => n, StringComparer.Ordinal))
        {
            data.Add(name);
        }
        return data;
    }

    [SkippableTheory]
    [MemberData(nameof(Cases))]
    public async Task Apply_then_diff_of_the_same_desired_state_is_empty(string scenario)
    {
        var schemaDir = Path.Combine(_dir, scenario);
        foreach (var (relativePath, sql) in Scenarios[scenario])
        {
            var path = Path.Combine(schemaDir, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, sql + "\nGO\n");
        }

        var applied = await ApplyOperation.RunAsync(
            _provider, _ledger, new ApplyOperation.Request(schemaDir, _db.Url));
        Assert.True(applied.Success, Render("apply failed", applied.Errors));

        var again = await DiffOperation.RunAsync(
            _provider, _ledger, schemaDir, _db.Url, allowDestructive: false);
        Assert.True(again.Success, Render("second diff failed", again.Errors));

        // Rendered rather than counted: a failure must say WHICH object re-diffed,
        // since that name is the whole diagnostic for a normalization mismatch.
        var residual = again.Plan!.Actions
            .Select(a => $"{a.Operation} {a.ObjectType} {a.ObjectName}")
            .OrderBy(l => l, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(Array.Empty<string>(), residual);
    }

    /// <summary>
    /// A known limitation, frozen deliberately (docs/limitations.md): SQL Server
    /// stores `CHECK (Status IN ('A','B','C'))` as an OR chain, and DacFx compares
    /// the two expressions as text — so the desired state and the database it just
    /// produced are read as different forever. Applying converges the database but
    /// never the plan.
    ///
    /// This asserts the defective behavior, not a desirable one. It exists so the
    /// limitation is measured rather than assumed, and so a DacFx upgrade that
    /// fixes it fails loudly here (same contract as the golden corpus; see the
    /// version policy in CONTRIBUTING).
    /// </summary>
    [SkippableFact]
    public async Task Check_constraints_written_as_IN_do_not_converge()
    {
        var schemaDir = Path.Combine(_dir, "check-in");
        Directory.CreateDirectory(Path.Combine(schemaDir, "tables"));
        File.WriteAllText(Path.Combine(schemaDir, "tables", "dbo.Flags.sql"), """
            CREATE TABLE dbo.Flags (
                Id INT NOT NULL PRIMARY KEY,
                Status CHAR(1) NOT NULL CONSTRAINT CK_Flags_Status CHECK (Status IN ('A', 'B', 'C'))
            );
            GO
            """);

        var applied = await ApplyOperation.RunAsync(
            _provider, _ledger, new ApplyOperation.Request(schemaDir, _db.Url));
        Assert.True(applied.Success, Render("apply failed", applied.Errors));
        // The database really did get the constraint — only the comparison disagrees.
        Assert.Equal(1, _db.Scalar<int>("SELECT COUNT(*) FROM sys.check_constraints WHERE name = 'CK_Flags_Status'"));

        var again = await DiffOperation.RunAsync(
            _provider, _ledger, schemaDir, _db.Url, allowDestructive: false);
        Assert.True(again.Success, Render("second diff failed", again.Errors));
        var action = Assert.Single(again.Plan!.Actions);
        Assert.Equal("Alter Table dbo.Flags", $"{action.Operation} {action.ObjectType} {action.ObjectName}");

        // The limitation is survivable only if the plan says which object is
        // churning. It used to say nothing — the change is announced by DacFx
        // under the constraint's name, so nothing attributed and `sql` was null,
        // leaving a reader with an unexplained permanent diff (cycle-57 → 61).
        Assert.NotNull(action.Sql);
        Assert.Contains("CK_Flags_Status", action.Sql);
    }

    private static string Render(string what, IReadOnlyList<RawMessage> errors) =>
        $"{what}: {string.Join(" | ", errors.Select(e => $"{e.Code} {e.Text}"))}";

    public void Dispose()
    {
        _db.Dispose();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp dir is not worth failing a test over.
        }
    }
}
