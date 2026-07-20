using Microsoft.Data.SqlClient;
using Schemorph.Core.Operations;
using Schemorph.Core.Providers;
using Schemorph.Provider.SqlServer;

namespace Schemorph.IntegrationTests;

/// <summary>
/// SCHEMORPH008 end to end. The unit test pins the message mapping; this pins the
/// thing the mapping rests on — which real connections actually produce an
/// incomplete comparison, and which only look like they should.
///
/// The four logins below are one axis, run against the same database and the same
/// desired state, differing only in what they may read. A structural change is
/// staged so completeness is observable rather than assumed: if the comparison saw
/// the target, it reports the changed table; if it did not, it reports nothing —
/// which is exactly the silence that must never read as "in sync".
///
/// Requires a server that accepts SQL logins. LocalDB in integrated-security-only
/// mode cannot make one, so the test skips there rather than failing.
/// </summary>
public sealed class RestrictedComparisonIntegrationTests : IDisposable
{
    private const string Password = "Sch3morph!IT#restricted";
    private const string LeastPrivilege = "schemorph_it_least_privilege";
    private const string Reader = "schemorph_it_reader";
    private const string ObjectDenied = "schemorph_it_object_denied";

    private readonly TestDatabase _db = new();
    private readonly string _dir = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), $"schemorph-restricted-{Guid.NewGuid():N}")).FullName;

    [SkippableFact]
    public async Task The_warning_tracks_what_the_comparison_could_read_not_which_permission_is_missing()
    {
        Skip.If(_db.Scalar<int>("SELECT CAST(SERVERPROPERTY('IsIntegratedSecurityOnly') AS INT)") != 0,
            "The server accepts Windows authentication only; restricted SQL logins cannot be created.");

        _db.Execute("CREATE TABLE dbo.Sales (Id INT NOT NULL PRIMARY KEY, Amount DECIMAL(18,2) NOT NULL)");
        _db.Execute("CREATE VIEW dbo.SalesSummary AS SELECT COUNT(*) AS Sales FROM dbo.Sales");

        // The desired state widens Amount, so a comparison that read the target has
        // exactly one change to report and one that did not has none.
        Write("tables/dbo.Sales.sql",
            "CREATE TABLE dbo.Sales (Id INT NOT NULL PRIMARY KEY, Amount DECIMAL(19,4) NOT NULL);\nGO\n");
        Write("views/dbo.SalesSummary.sql",
            "CREATE VIEW dbo.SalesSummary AS SELECT COUNT(*) AS Sales FROM dbo.Sales\nGO\n");

        // db_owner without the server-scoped grant: the shape docs/security.md
        // recommends, and the one a consumer reported the false warning from.
        CreateLogin(LeastPrivilege);
        _db.Execute($"ALTER ROLE db_owner ADD MEMBER [{LeastPrivilege}]");

        // Reads nothing: no VIEW DEFINITION anywhere.
        CreateLogin(Reader);
        _db.Execute($"ALTER ROLE db_datareader ADD MEMBER [{Reader}]");
        _db.Execute($"DENY VIEW DEFINITION TO [{Reader}]");

        // Granted at database scope, denied on the object that changed — the case
        // that rules out checking the database-scoped permission instead.
        CreateLogin(ObjectDenied);
        _db.Execute($"ALTER ROLE db_datareader ADD MEMBER [{ObjectDenied}]");
        _db.Execute($"GRANT VIEW DEFINITION TO [{ObjectDenied}]");
        _db.Execute($"DENY VIEW DEFINITION ON OBJECT::dbo.Sales TO [{ObjectDenied}]");

        var provider = new SqlServerProvider();
        var state = await provider.LoadDesiredStateAsync(_dir);
        Assert.Empty(state.Errors);

        async Task<CompareResult> Compare(string url) =>
            await provider.CompareAsync(new CompareRequest(state, url));

        var owner = await Compare(_db.Url);
        var leastPrivilege = await Compare(UrlFor(LeastPrivilege));
        var reader = await Compare(UrlFor(Reader));
        var objectDenied = await Compare(UrlFor(ObjectDenied));

        // Reference: an unrestricted connection sees the change and says nothing about restriction.
        Assert.Single(owner.Changes);
        Assert.DoesNotContain(owner.Messages, m => m.Code == "SCHEMORPH008");

        // The regression. This login lacks the server-scoped VIEW ANY DEFINITION and
        // DacFx says so, but it reads every database object — the plan is complete,
        // so claiming otherwise is a false alarm on the recommended setup.
        Assert.Equal(0, Scalar(UrlFor(LeastPrivilege),
            "SELECT HAS_PERMS_BY_NAME(NULL, NULL, 'VIEW ANY DEFINITION')"));
        Assert.Contains(leastPrivilege.Messages,
            m => m.Text.Contains("VIEW ANY DEFINITION", StringComparison.OrdinalIgnoreCase));
        Assert.Single(leastPrivilege.Changes);
        Assert.DoesNotContain(leastPrivilege.Messages, m => m.Code == "SCHEMORPH008");

        // Genuinely blind: the change is gone, and that silence is called out.
        Assert.Empty(reader.Changes);
        Assert.Contains(reader.Messages, m => m.Code == "SCHEMORPH008" && m.Severity == "Warning");

        // Granted at database scope and still blind — the false negative a
        // permission check would produce.
        Assert.Equal(1, Scalar(UrlFor(ObjectDenied),
            "SELECT HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'VIEW DEFINITION')"));
        Assert.Empty(objectDenied.Changes);
        Assert.Contains(objectDenied.Messages, m => m.Code == "SCHEMORPH008" && m.Severity == "Warning");
    }

    /// <summary>
    /// What the verbs do with the above, which is the part a consumer actually
    /// meets: an incomplete comparison never becomes a plan. `diff` fails at the
    /// compare stage — it does not hand back an empty plan with a caveat attached,
    /// so there is no "no changes" for automation to misread as in-sync. (`apply`
    /// refuses on the same errors.) The complete least-privilege connection plans
    /// normally, which is the whole point of the distinction.
    ///
    /// This is the guardrail: if `diff` is ever changed to warn-and-succeed on an
    /// unreadable target, that would be a silent-partial-plan regression, and this
    /// test is what catches it.
    /// </summary>
    [SkippableFact]
    public async Task An_incomplete_comparison_fails_the_diff_instead_of_planning()
    {
        Skip.If(_db.Scalar<int>("SELECT CAST(SERVERPROPERTY('IsIntegratedSecurityOnly') AS INT)") != 0,
            "The server accepts Windows authentication only; restricted SQL logins cannot be created.");

        _db.Execute("CREATE TABLE dbo.Sales (Id INT NOT NULL PRIMARY KEY, Amount DECIMAL(18,2) NOT NULL)");
        Write("tables/dbo.Sales.sql",
            "CREATE TABLE dbo.Sales (Id INT NOT NULL PRIMARY KEY, Amount DECIMAL(19,4) NOT NULL);\nGO\n");

        CreateLogin(LeastPrivilege);
        _db.Execute($"ALTER ROLE db_owner ADD MEMBER [{LeastPrivilege}]");
        CreateLogin(Reader);
        _db.Execute($"ALTER ROLE db_datareader ADD MEMBER [{Reader}]");
        _db.Execute($"DENY VIEW DEFINITION TO [{Reader}]");

        var provider = new SqlServerProvider();
        var ledger = new SqlServerLedgerStore();

        var blind = await DiffOperation.RunAsync(provider, ledger, _dir, UrlFor(Reader), false);
        Assert.False(blind.Success);
        Assert.Equal(DiffOperation.FailureStage.Compare, blind.Stage);
        Assert.Null(blind.Plan);
        Assert.Contains(blind.Errors, m => m.Code == "SCHEMORPH008");

        var complete = await DiffOperation.RunAsync(provider, ledger, _dir, UrlFor(LeastPrivilege), false);
        Assert.True(complete.Success);
        Assert.Single(complete.Plan!.Actions);
    }

    private void Write(string relativePath, string content)
    {
        var path = Path.Combine(_dir, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private void CreateLogin(string name)
    {
        DropLoginIfPresent(name);
        _db.ExecuteOnServer($"CREATE LOGIN [{name}] WITH PASSWORD = '{Password}', CHECK_POLICY = OFF");
        _db.Execute($"CREATE USER [{name}] FOR LOGIN [{name}]");
    }

    private string UrlFor(string login) => new SqlConnectionStringBuilder(_db.Url)
    {
        IntegratedSecurity = false,
        UserID = login,
        Password = Password,
    }.ConnectionString;

    private static int Scalar(string connectionString, string sql)
    {
        using var connection = new SqlConnection(connectionString);
        connection.Open();
        using var command = new SqlCommand(sql, connection);
        var value = command.ExecuteScalar();
        return value is null or DBNull ? -1 : Convert.ToInt32(value);
    }

    private void DropLoginIfPresent(string name) => _db.ExecuteOnServer(
        $"IF EXISTS (SELECT 1 FROM sys.server_principals WHERE name = '{name}') DROP LOGIN [{name}]");

    public void Dispose()
    {
        // Pooled connections under these logins keep DROP LOGIN from succeeding.
        SqlConnection.ClearAllPools();
        foreach (var login in new[] { LeastPrivilege, Reader, ObjectDenied })
        {
            try
            {
                DropLoginIfPresent(login);
            }
            catch (SqlException)
            {
                // The database goes either way; a leftover login on a throwaway
                // server is not worth failing a test over.
            }
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
