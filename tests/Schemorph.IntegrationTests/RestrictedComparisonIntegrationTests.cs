using Microsoft.Data.SqlClient;
using Schemorph.Core.Providers;
using Schemorph.Provider.SqlServer;

namespace Schemorph.IntegrationTests;

/// <summary>
/// SCHEMORPH008 end to end: a login that cannot read object definitions makes
/// DacFx silently restrict the comparison, so changes to what it could not see
/// are omitted — a partial result that must never read as "in sync".
///
/// The unit test pins the message mapping; this pins the thing that actually
/// matters — that a real restricted connection produces the DacFx signal the
/// mapping is looking for. Without it, the behavior rests on the assumption that
/// the pinned DacFx still words the message the same way.
///
/// Requires a server that accepts SQL logins. LocalDB in integrated-security-only
/// mode cannot make one, so the test skips there rather than failing.
/// </summary>
public sealed class RestrictedComparisonIntegrationTests : IDisposable
{
    private const string LoginName = "schemorph_it_restricted";
    private const string Password = "Sch3morph!IT#restricted";

    private readonly TestDatabase _db = new();
    private readonly string _dir = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), $"schemorph-restricted-{Guid.NewGuid():N}")).FullName;

    [SkippableFact]
    public async Task A_login_without_VIEW_ANY_DEFINITION_gets_an_incompleteness_warning()
    {
        Skip.If(_db.Scalar<int>("SELECT CAST(SERVERPROPERTY('IsIntegratedSecurityOnly') AS INT)") != 0,
            "The server accepts Windows authentication only; a restricted SQL login cannot be created.");

        // A view whose definition the restricted login must not be able to read.
        _db.Execute("CREATE TABLE dbo.Sales (Id INT NOT NULL PRIMARY KEY, Amount DECIMAL(18,2) NOT NULL)");
        _db.Execute("CREATE VIEW dbo.SalesSummary AS SELECT COUNT(*) AS Sales FROM dbo.Sales");

        DropLoginIfPresent();
        _db.ExecuteOnServer($"CREATE LOGIN [{LoginName}] WITH PASSWORD = '{Password}', CHECK_POLICY = OFF");
        _db.Execute($"CREATE USER [{LoginName}] FOR LOGIN [{LoginName}]");
        _db.Execute($"ALTER ROLE db_datareader ADD MEMBER [{LoginName}]");
        // The restriction under test. A plain reader already lacks VIEW ANY
        // DEFINITION, so the DENY is belt and braces rather than the operative
        // cause — what proves the behavior is the owner connection below, which
        // must NOT warn.
        _db.Execute($"DENY VIEW DEFINITION TO [{LoginName}]");

        var restrictedUrl = new SqlConnectionStringBuilder(_db.Url)
        {
            IntegratedSecurity = false,
            UserID = LoginName,
            Password = Password,
        }.ConnectionString;

        Directory.CreateDirectory(Path.Combine(_dir, "tables"));
        File.WriteAllText(Path.Combine(_dir, "tables", "dbo.Sales.sql"),
            "CREATE TABLE dbo.Sales (Id INT NOT NULL PRIMARY KEY, Amount DECIMAL(18,2) NOT NULL);\nGO\n");

        var provider = new SqlServerProvider();
        var state = await provider.LoadDesiredStateAsync(_dir);
        Assert.Empty(state.Errors);

        var restricted = await provider.CompareAsync(new CompareRequest(state, restrictedUrl));
        Assert.Contains(restricted.Messages, m => m.Code == "SCHEMORPH008" && m.Severity == "Warning");

        // The other half of the claim: the warning tracks the restriction, it is
        // not emitted on every comparison. Same desired state, same database, a
        // connection that can read definitions — and the plan says nothing.
        var unrestricted = await provider.CompareAsync(new CompareRequest(state, _db.Url));
        Assert.DoesNotContain(unrestricted.Messages, m => m.Code == "SCHEMORPH008");
    }

    private void DropLoginIfPresent()
    {
        _db.ExecuteOnServer(
            $"IF EXISTS (SELECT 1 FROM sys.server_principals WHERE name = '{LoginName}') DROP LOGIN [{LoginName}]");
    }

    public void Dispose()
    {
        try
        {
            DropLoginIfPresent();
        }
        catch (SqlException)
        {
            // The database is dropped below either way; a leftover login on a
            // throwaway CI server is not worth failing a test over.
        }
        _db.Dispose();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
