// Phase 0 spike: idempotent CREATE OR ALTER re-definition flow with
// dependency ordering on a realistic programmable-object set.
// Validates:
//   1. Dependency ordering is load-bearing (naive alphabetical order FAILS)
//   2. TSqlModel.GetReferenced() yields a correct topological order
//   3. CREATE -> CREATE OR ALTER rewrite + re-application is idempotent
// Exit codes: 0 = all validations passed, 1 = validation failed.

using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.SqlServer.Dac.Model;

var fixturesDir = args.Length > 0 ? args[0] : "fixtures";
var connectionString = args.Length > 1
    ? args[1]
    : @"Data Source=(localdb)\MSSQLLocalDB;Initial Catalog=SchemorphSpike;Integrated Security=True;Encrypt=False";

var programmableFiles = Directory
    .GetFiles(Path.Combine(fixturesDir, "programmable"), "*.sql", SearchOption.AllDirectories)
    .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)   // deliberate naive order
    .ToArray();

// --- 1. Build the model: structural prereqs + programmable objects -----------
using var model = new TSqlModel(SqlServerVersion.Sql150, new TSqlModelOptions());
foreach (var file in Directory.GetFiles(fixturesDir, "*.sql", SearchOption.AllDirectories))
{
    foreach (var batch in SplitBatches(File.ReadAllText(file)))
    {
        model.AddObjects(batch);
    }
}

var modelErrors = model.Validate().Where(m => m.MessageType == Microsoft.SqlServer.Dac.DacMessageType.Error).ToList();
if (modelErrors.Count > 0)
{
    modelErrors.ForEach(e => Console.Error.WriteLine(e.Message));
    return 1;
}

// --- 2. Extract the programmable dependency graph from the model -------------
var programmable = model
    .GetObjects(DacQueryScopes.UserDefined,
        Procedure.TypeClass, View.TypeClass, ScalarFunction.TypeClass, TableValuedFunction.TypeClass)
    .ToDictionary(o => string.Join(".", o.Name.Parts), o => o, StringComparer.OrdinalIgnoreCase);

var edges = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);   // node -> prerequisites
foreach (var (name, obj) in programmable)
{
    edges[name] = obj.GetReferenced(DacQueryScopes.UserDefined)
        .Select(r => string.Join(".", r.Name.Parts))
        .Where(r => !r.Equals(name, StringComparison.OrdinalIgnoreCase) && programmable.ContainsKey(r))
        .ToHashSet(StringComparer.OrdinalIgnoreCase);
}

// Kahn topological sort (stable: alphabetical among ready nodes).
var order = new List<string>();
var remaining = edges.ToDictionary(kv => kv.Key, kv => new HashSet<string>(kv.Value, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
while (remaining.Count > 0)
{
    var ready = remaining.Where(kv => kv.Value.Count == 0).Select(kv => kv.Key)
        .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
    if (ready.Count == 0)
    {
        Console.Error.WriteLine("Dependency cycle detected among: " + string.Join(", ", remaining.Keys));
        return 1;
    }
    foreach (var node in ready)
    {
        order.Add(node);
        remaining.Remove(node);
        foreach (var deps in remaining.Values) deps.Remove(node);
    }
}

// Map object name -> file (convention: file name == object name).
var fileByObject = programmableFiles.ToDictionary(
    f => "dbo." + Path.GetFileNameWithoutExtension(f), f => f, StringComparer.OrdinalIgnoreCase);

// --- 3. Prepare the target database ------------------------------------------
await using var connection = new SqlConnection(connectionString);
await connection.OpenAsync();
await Execute(connection, """
    IF OBJECT_ID('dbo.Customers') IS NULL
        CREATE TABLE dbo.Customers (Id INT NOT NULL CONSTRAINT PK_Customers PRIMARY KEY, Name NVARCHAR(100) NOT NULL);
    IF COL_LENGTH('dbo.Customers', 'Email') IS NULL
        ALTER TABLE dbo.Customers ADD Email NVARCHAR(256) NULL;
    IF OBJECT_ID('dbo.Orders') IS NULL
    BEGIN
        CREATE TABLE dbo.Orders (Id INT NOT NULL CONSTRAINT PK_Orders PRIMARY KEY, CustomerId INT NOT NULL,
            PlacedAt DATETIME2(3) NOT NULL, Total DECIMAL(18,2) NOT NULL,
            CONSTRAINT FK_Orders_Customers FOREIGN KEY (CustomerId) REFERENCES dbo.Customers (Id));
    END
    IF NOT EXISTS (SELECT 1 FROM dbo.Customers WHERE Id = 1)
        INSERT dbo.Customers (Id, Name) VALUES (1, N'Ada'), (2, N'Grace');
    IF NOT EXISTS (SELECT 1 FROM dbo.Orders WHERE Id = 1)
        INSERT dbo.Orders (Id, CustomerId, PlacedAt, Total) VALUES
            (1, 1, SYSUTCDATETIME(), 120.50), (2, 1, SYSUTCDATETIME(), 79.90);
    """);
// Clean slate for the programmable set so the naive-order failure is reproducible.
foreach (var name in Enumerable.Reverse(order))
    await Execute(connection, $"DROP OBJECT IF EXISTS {name};".Replace("DROP OBJECT", DropKeyword(programmable[name])));

// --- 4. Prove naive (alphabetical) order fails --------------------------------
string? naiveFailure = null;
foreach (var file in programmableFiles)
{
    try { await Execute(connection, RewriteToCreateOrAlter(ReadSingleBatch(file))); }
    catch (SqlException ex) { naiveFailure = $"{Path.GetFileName(file)}: {ex.Message}"; break; }
}
// Clean up whatever the naive pass managed to create.
foreach (var name in Enumerable.Reverse(order))
    await Execute(connection, $"DROP OBJECT IF EXISTS {name};".Replace("DROP OBJECT", DropKeyword(programmable[name])));

// --- 5. Apply in dependency order, twice (idempotency) ------------------------
var rounds = new List<string>();
for (var round = 1; round <= 2; round++)
{
    foreach (var name in order)
        await Execute(connection, RewriteToCreateOrAlter(ReadSingleBatch(fileByObject[name])));
    rounds.Add($"round {round}: OK ({order.Count} objects)");
}

// --- 6. Functional check -------------------------------------------------------
await using var cmd = new SqlCommand("EXEC dbo.GetCustomerSummary @CustomerId = 1;", connection);
await using var reader = await cmd.ExecuteReaderAsync();
var functionalRows = 0;
decimal lifetimeTotal = 0;
while (await reader.ReadAsync()) { functionalRows++; lifetimeTotal = reader.GetDecimal(3); }

var report = new
{
    dependencyOrder = order,
    naiveOrderFailed = naiveFailure is not null,
    naiveFailure,
    idempotentApply = rounds,
    functional = new { rows = functionalRows, lifetimeTotal },
};
Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));

var pass = naiveFailure is not null && rounds.Count == 2 && functionalRows == 1 && lifetimeTotal == 200.40m;
Console.WriteLine(pass ? "SPIKE PASS" : "SPIKE FAIL");
return pass ? 0 : 1;

static string DropKeyword(TSqlObject obj) =>
    obj.ObjectType.Name switch
    {
        "Procedure" => "DROP PROCEDURE",
        "View" => "DROP VIEW",
        _ => "DROP FUNCTION",
    };

static string RewriteToCreateOrAlter(string sql) =>
    Regex.Replace(sql, @"\bCREATE\s+(PROCEDURE|PROC|FUNCTION|VIEW|TRIGGER)\b",
        "CREATE OR ALTER $1", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(5));

static string ReadSingleBatch(string file) => SplitBatches(File.ReadAllText(file)).First();

static async Task Execute(SqlConnection connection, string sql)
{
    await using var cmd = new SqlCommand(sql, connection);
    await cmd.ExecuteNonQueryAsync();
}

static IEnumerable<string> SplitBatches(string script)
{
    var batch = new List<string>();
    foreach (var line in script.Replace("\r\n", "\n").Split('\n'))
    {
        if (line.Trim().Equals("GO", StringComparison.OrdinalIgnoreCase))
        {
            if (batch.Count > 0) yield return string.Join('\n', batch);
            batch.Clear();
        }
        else
        {
            batch.Add(line);
        }
    }
    if (batch.Any(l => l.Trim().Length > 0)) yield return string.Join('\n', batch);
}
