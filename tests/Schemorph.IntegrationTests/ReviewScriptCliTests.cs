using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Schemorph.IntegrationTests;

/// <summary>
/// <c>diff --format sql</c> through the real binary and against a real database.
/// The unit tests pin the document's shape; these pin the thing only a real
/// comparison can show — that the declarative text in the document is the engine's
/// own update script, and that the hash in its header is the hash the apply gate
/// accepts. A review artifact whose hash did not match would be worse than none.
/// </summary>
public sealed class ReviewScriptCliTests : IDisposable
{
    private readonly TestDatabase _db = new();
    private readonly string _dir = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), $"schemorph-review-{Guid.NewGuid():N}")).FullName;

    private string SchemaDir => Path.Combine(_dir, "schema");

    private static string CliDll => Path.Combine(AppContext.BaseDirectory, "schemorph.dll");

    private sealed record CliResult(int ExitCode, string StdOut, string StdErr);

    private static CliResult Run(string arguments)
    {
        var psi = new ProcessStartInfo("dotnet", $"exec \"{CliDll}\" {arguments}")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return new CliResult(process.ExitCode, stdout, stderr);
    }

    /// <summary>A table to create and a view over it — one change in each stage.</summary>
    private void SeedSchema()
    {
        Directory.CreateDirectory(Path.Combine(SchemaDir, "tables"));
        Directory.CreateDirectory(Path.Combine(SchemaDir, "views"));
        File.WriteAllText(Path.Combine(SchemaDir, "tables", "dbo.Orders.sql"),
            "CREATE TABLE dbo.Orders (Id INT NOT NULL PRIMARY KEY, Total DECIMAL(18,2) NOT NULL);");
        File.WriteAllText(Path.Combine(SchemaDir, "views", "dbo.VOrders.sql"),
            "CREATE VIEW dbo.VOrders AS SELECT Id, Total FROM dbo.Orders;");
    }

    /// <summary>
    /// A target an earlier apply wrote a history ledger into — every target after its first apply.
    /// The engine compares the whole database, so its update script carries statements for the ledger
    /// table the desired state does not declare; the plan must name it as not executed, the review
    /// document must say so above the script, the hash must still be the gate's, and the apply must
    /// leave the ledger and its history in place.
    /// </summary>
    [SkippableFact]
    public void A_ledger_from_an_earlier_apply_is_named_as_not_executed_and_survives_the_gated_apply()
    {
        SeedSchema();
        Assert.Equal(0, Run($"apply --url \"{_db.Url}\" --schema \"{SchemaDir}\"").ExitCode);
        var historyRows = _db.Scalar<int>("SELECT COUNT(*) FROM dbo.__SchemorphHistory");
        Assert.True(historyRows > 0);

        // A second change, reviewed against the target that now holds the ledger.
        File.WriteAllText(Path.Combine(SchemaDir, "tables", "dbo.Orders.sql"),
            "CREATE TABLE dbo.Orders (Id INT NOT NULL PRIMARY KEY, Total DECIMAL(18,2) NOT NULL, Note NVARCHAR(50) NULL);");

        var json = JsonDocument.Parse(
            Run($"diff --url \"{_db.Url}\" --schema \"{SchemaDir}\" --format json").StdOut).RootElement;
        var planHash = json.GetProperty("planHash").GetString()!;
        Assert.Contains(json.GetProperty("excluded").EnumerateArray(),
            e => e.GetProperty("objectName").GetString() == "dbo.__SchemorphHistory");
        Assert.DoesNotContain(json.GetProperty("changes").EnumerateArray(),
            c => c.GetProperty("objectName").GetString() == "dbo.__SchemorphHistory");

        var document = Run($"diff --url \"{_db.Url}\" --schema \"{SchemaDir}\" --format sql").StdOut;
        var notExecuted = document.IndexOf("NOT EXECUTED", StringComparison.Ordinal);
        Assert.True(notExecuted >= 0, "the document must say what it contains but will not run");
        Assert.True(document.IndexOf("__SchemorphHistory", notExecuted, StringComparison.Ordinal) > notExecuted);
        Assert.Contains($"planHash:  {planHash}", document);

        Assert.Equal(0, Run($"apply --url \"{_db.Url}\" --schema \"{SchemaDir}\" --expect-plan {planHash}").ExitCode);
        Assert.True(_db.Scalar<int>("SELECT COUNT(*) FROM dbo.__SchemorphHistory") > historyRows,
            "the ledger survives the apply and records it");
        Assert.Equal(1, _db.Scalar<int>(
            "SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID('dbo.Orders') AND name = 'Note'"));
    }

    [SkippableFact]
    public void The_document_covers_every_stage_and_its_hash_is_the_gate_hash()
    {
        SeedSchema();

        var sql = Run($"diff --url \"{_db.Url}\" --schema \"{SchemaDir}\" --format sql");
        Assert.Equal(2, sql.ExitCode);   // changes pending

        // Both stages present: a document holding only part of the plan is
        // worthless as an approval gate.
        Assert.Contains("CREATE TABLE", sql.StdOut, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("dbo.VOrders", sql.StdOut);
        Assert.Contains("READ ONLY", sql.StdOut);

        // The header's hash is the JSON plan's planHash — the reviewed paper and
        // the enforced fingerprint are one artifact.
        var json = Run($"diff --url \"{_db.Url}\" --schema \"{SchemaDir}\" --format json");
        var planHash = JsonDocument.Parse(json.StdOut).RootElement.GetProperty("planHash").GetString()!;
        Assert.Contains($"planHash:  {planHash}", sql.StdOut);
        Assert.Contains($"--expect-plan {planHash}", sql.StdOut);

        // And that hash is accepted by the gate it advertises.
        var applied = Run($"apply --url \"{_db.Url}\" --schema \"{SchemaDir}\" --expect-plan {planHash}");
        Assert.Equal(0, applied.ExitCode);
    }

    [SkippableFact]
    public void The_password_never_reaches_the_document()
    {
        SeedSchema();
        var url = _db.Url.Contains("Password", StringComparison.OrdinalIgnoreCase)
            ? _db.Url
            : _db.Url + ";Password=hunter2";

        var sql = Run($"diff --url \"{url}\" --schema \"{SchemaDir}\" --format sql");

        // The document is meant to be archived and circulated for signature.
        Assert.DoesNotContain("hunter2", sql.StdOut);
    }

    [SkippableFact]
    public void A_converged_database_renders_a_document_that_says_nothing_to_do()
    {
        SeedSchema();
        Run($"apply --url \"{_db.Url}\" --schema \"{SchemaDir}\"");

        var sql = Run($"diff --url \"{_db.Url}\" --schema \"{SchemaDir}\" --format sql");

        Assert.Equal(0, sql.ExitCode);
        Assert.Contains("No changes.", sql.StdOut);
    }

    [SkippableFact]
    public void Sql_is_refused_on_the_verbs_that_produce_no_document()
    {
        SeedSchema();

        foreach (var verb in new[] { "apply", "status", "inspect" })
        {
            var result = Run($"{verb} --url \"{_db.Url}\" --schema \"{SchemaDir}\" " +
                             $"--out \"{Path.Combine(_dir, "o")}\" --format sql");

            // Accepting the flag and silently rendering text would advertise an
            // output this verb cannot produce.
            Assert.Equal(1, result.ExitCode);
            Assert.Contains("only available on diff", result.StdErr);
        }
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }
}
