// Phase 0 spike: drive DacFx SchemaComparison headlessly.
// Pipeline under test = Schemorph's intended execution path:
//   desired-state .sql files -> TSqlModel -> in-memory-built dacpac
//   -> SchemaComparison(dacpac source, live database target)
//   -> structured differences + generated update script.
// Exit codes: 0 = compared OK, 1 = model/validation errors, 2 = comparison errors.

using System.Diagnostics;
using System.Text.Json;
using Microsoft.SqlServer.Dac;
using Microsoft.SqlServer.Dac.Compare;
using Microsoft.SqlServer.Dac.Model;

var desiredStateDir = args.Length > 0 ? args[0] : Path.Combine("fixtures", "desired-state");
var connectionString = args.Length > 1
    ? args[1]
    : @"Data Source=(localdb)\MSSQLLocalDB;Initial Catalog=SchemorphSpike;Integrated Security=True;Encrypt=False";
var targetDatabaseName = args.Length > 2 ? args[2] : "SchemorphSpike";

var stopwatch = Stopwatch.StartNew();

// 1. Load desired state from plain .sql files into a TSqlModel.
//    Sql150 matches the LocalDB target instance (SQL Server 2019).
using var model = new TSqlModel(SqlServerVersion.Sql150, new TSqlModelOptions());
var sqlFiles = Directory.GetFiles(desiredStateDir, "*.sql", SearchOption.AllDirectories)
    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
    .ToArray();

foreach (var file in sqlFiles)
{
    // AddObjects does not understand the GO batch separator; split batches first.
    foreach (var batch in SplitBatches(File.ReadAllText(file)))
    {
        model.AddObjects(batch);
    }
}

var modelLoadMs = stopwatch.ElapsedMilliseconds;

// 2. Validate the model before building.
var validationErrors = model.Validate()
    .Where(m => m.MessageType == DacMessageType.Error)
    .Select(m => $"{m.Prefix}{m.Number} [{m.ElementType}]: {m.Message}")
    .ToList();
if (validationErrors.Count > 0)
{
    Console.Error.WriteLine("Desired-state model has validation errors:");
    validationErrors.ForEach(Console.Error.WriteLine);
    return 1;
}

// 3. Build a dacpac from the model (DacFx compare endpoints take dacpac/db/project).
var dacpacPath = Path.Combine(Path.GetTempPath(), $"schemorph-spike-{Guid.NewGuid():N}.dacpac");
DacPackageExtensions.BuildPackage(dacpacPath, model,
    new PackageMetadata { Name = "SchemorphSpikeDesiredState", Version = "0.0.1" });
var buildDacpacMs = stopwatch.ElapsedMilliseconds - modelLoadMs;

try
{
    // 4. Compare: desired state (source) vs live database (target).
    var comparison = new SchemaComparison(
        new SchemaCompareDacpacEndpoint(dacpacPath),
        new SchemaCompareDatabaseEndpoint(connectionString));

    // Spike note: with defaults, DacFx blocks the LegacyLog drop with
    // "data loss could occur" — exactly the hook Schemorph's destructive
    // gating maps onto. Here we opt in (≈ future `--allow-destructive`)
    // to observe the full plan including the DROP.
    comparison.Options.BlockOnPossibleDataLoss = false;
    comparison.Options.DropObjectsNotInSource = true;

    var result = comparison.Compare();
    var compareMs = stopwatch.ElapsedMilliseconds - modelLoadMs - buildDacpacMs;

    // GetErrors() also surfaces non-fatal messages (e.g. the data-loss
    // notice for a DROP) — only genuine errors abort the spike; the rest
    // is exactly the risk-classification input Schemorph's plan wants.
    var messages = result.GetErrors()
        .Select(e => new { severity = e.MessageType.ToString(), code = $"{e.Prefix}{e.Number}", message = e.Message })
        .ToArray();
    if (messages.Any(m => m.severity == nameof(DacMessageType.Error)))
    {
        Console.Error.WriteLine("Comparison reported errors:");
        foreach (var m in messages) Console.Error.WriteLine($"  [{m.severity}] {m.code} {m.message}");
        return 2;
    }

    // 5. Structured extraction — the part Schemorph's plan model will consume.
    var differences = result.Differences.Select(d => new
    {
        action = d.UpdateAction.ToString(),                       // Add | Change | Delete
        objectType = (d.SourceObject ?? d.TargetObject)?.ObjectType.Name,
        // SchemaDifference.Name is the type display name; the real object
        // identity lives on the model objects' ObjectIdentifier.
        name = ((d.SourceObject ?? d.TargetObject)?.Name.Parts is { Count: > 0 } parts)
            ? string.Join(".", parts)
            : null,
        included = d.Included,
    }).ToArray();

    // 6. Generate the update script (what `apply` would run).
    var scriptResult = result.GenerateScript(targetDatabaseName);

    var report = new
    {
        formatVersion = "spike-0",
        desiredStateFiles = sqlFiles.Length,
        timingsMs = new { modelLoad = modelLoadMs, buildDacpac = buildDacpacMs, compare = compareMs },
        equal = result.IsEqual,
        messages,
        differences,
        script = new
        {
            success = scriptResult.Success,
            message = scriptResult.Message,
            length = scriptResult.Script?.Length ?? 0,
        },
    };
    Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));

    Console.WriteLine("--- generated update script ---");
    Console.WriteLine(scriptResult.Script);
    return 0;
}
finally
{
    File.Delete(dacpacPath);
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
