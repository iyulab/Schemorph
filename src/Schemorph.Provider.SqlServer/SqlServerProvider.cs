using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.SqlServer.Dac;
using Microsoft.SqlServer.Dac.Compare;
using Microsoft.SqlServer.Dac.Model;
using Schemorph.Core.Providers;
using Index = Microsoft.SqlServer.Dac.Model.Index;

namespace Schemorph.Provider.SqlServer;

/// <summary>
/// SQL Server provider backed by DacFx (ADR-0001).
/// Compare/apply pipeline (validated in Phase 0, spikes/dacfx-headless):
///   desired-state .sql files → TSqlModel → temp dacpac
///   → SchemaComparison(dacpac source, database target) → raw changes / publish.
/// The provider reports and applies *mechanically*; risk policy (destructive
/// gating, self-exclusion) is injected by the core via the include hook.
/// </summary>
public sealed class SqlServerProvider : IDatabaseProvider
{
    // TODO(schemorph.json): make the model version configurable per project.
    private const SqlServerVersion ModelVersion = SqlServerVersion.Sql150;

    public string Name => "sqlserver";

    public Task<InspectResult> InspectAsync(InspectRequest request, CancellationToken cancellationToken = default)
        => Task.Run(() => Inspect(request, cancellationToken), cancellationToken);

    public Task<CompareResult> CompareAsync(CompareRequest request, CancellationToken cancellationToken = default)
        => Task.Run(() => Compare(request, cancellationToken), cancellationToken);

    public Task<ApplyResult> ApplyAsync(ApplyRequest request, Func<RawChange, bool> includeChange, Action<IReadOnlyList<RawChange>>? onChangesComputed = null, CancellationToken cancellationToken = default)
        => Task.Run(() => Apply(request, includeChange, onChangesComputed, cancellationToken), cancellationToken);

    public Task<ProgrammableAnalysis> AnalyzeProgrammablesAsync(string desiredStateDirectory, CancellationToken cancellationToken = default)
        => Task.Run(() => AnalyzeProgrammables(desiredStateDirectory), cancellationToken);

    public async Task<IReadOnlyList<ProgrammableObjectInfo>> FilterMatchingLiveDefinitionsAsync(
        string connectionString, IReadOnlyList<ProgrammableObjectInfo> objects, CancellationToken cancellationToken = default)
    {
        if (objects.Count == 0) return Array.Empty<ProgrammableObjectInfo>();

        // sys.sql_modules stores the deployed batch text verbatim for every
        // programmable kind (views, procedures, functions, triggers).
        var live = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        await using (var connection = new SqlConnection(connectionString))
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = new SqlCommand("""
                SELECT s.name + '.' + o.name, m.definition
                FROM sys.sql_modules m
                JOIN sys.objects o ON o.object_id = m.object_id
                JOIN sys.schemas s ON s.schema_id = o.schema_id
                WHERE m.definition IS NOT NULL
                """, connection);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                live[reader.GetString(0)] = reader.GetString(1);
            }
        }

        return objects
            .Where(o => live.TryGetValue(o.ObjectName, out var definition)
                        && LiveDefinitionMatcher.Matches(File.ReadAllText(o.FilePath), definition))
            .ToList();
    }

    public Task ExecuteScriptAsync(string connectionString, string script, CancellationToken cancellationToken = default)
        => ExecuteScriptAsync(connectionString, script, Array.Empty<Schemorph.Core.Ledger.LedgerEntry>(), cancellationToken);

    public async Task ExecuteScriptAsync(
        string connectionString, string script,
        IReadOnlyList<Schemorph.Core.Ledger.LedgerEntry> ledgerEntries, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        try
        {
            foreach (var batch in SqlBatchSplitter.Split(script))
            {
                await using var command = new SqlCommand(batch, connection, transaction);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            // ADR-0004: the ledger rows commit with the script or not at all.
            foreach (var entry in ledgerEntries)
            {
                await LedgerSql.InsertAsync(connection, transaction, entry, cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    // ------------------------------------------------------------------ compare

    private static CompareResult Compare(CompareRequest request, CancellationToken cancellationToken)
    {
        using var session = ComparisonSession.Open(request.DesiredStateDirectory, request.ConnectionString, cancellationToken);
        if (session.ModelErrors.Count > 0)
        {
            return new CompareResult(Array.Empty<RawChange>(), session.ModelErrors, UpdateScript: null);
        }

        var result = session.Result!;
        var messages = session.LoadWarnings.Concat(CollectMessages(result)).ToList();
        if (messages.Any(m => m.Severity == nameof(DacMessageType.Error)))
        {
            return new CompareResult(Array.Empty<RawChange>(), messages, UpdateScript: null);
        }

        var changes = result.Differences.Select(ToRawChange).ToList();

        string? script = null;
        if (changes.Count > 0)
        {
            var databaseName = new SqlConnectionStringBuilder(request.ConnectionString).InitialCatalog;
            var scriptResult = result.GenerateScript(databaseName);
            if (scriptResult.Success)
            {
                script = scriptResult.Script;
            }
            else
            {
                messages.Add(new RawMessage("Warning", "SCHEMORPH002",
                    $"Update script generation failed: {scriptResult.Message}"));
            }
        }

        return new CompareResult(changes, messages, script);
    }

    // ------------------------------------------------------------------ apply

    private static ApplyResult Apply(ApplyRequest request, Func<RawChange, bool> includeChange, Action<IReadOnlyList<RawChange>>? onChangesComputed, CancellationToken cancellationToken)
    {
        using var session = ComparisonSession.Open(request.DesiredStateDirectory, request.ConnectionString, cancellationToken);
        if (session.ModelErrors.Count > 0)
        {
            return new ApplyResult(false, Array.Empty<RawChange>(), Array.Empty<RawChange>(), session.ModelErrors);
        }

        var result = session.Result!;
        var messages = session.LoadWarnings.Concat(CollectMessages(result)).ToList();
        if (messages.Any(m => m.Severity == nameof(DacMessageType.Error)))
        {
            return new ApplyResult(false, Array.Empty<RawChange>(), Array.Empty<RawChange>(), messages);
        }

        var applied = new List<RawChange>();
        var excluded = new List<RawChange>();
        var all = new List<RawChange>();
        foreach (var difference in result.Differences)
        {
            var change = ToRawChange(difference);
            all.Add(change);
            if (includeChange(change))
            {
                applied.Add(change);
            }
            else
            {
                excluded.Add(change);
                result.Exclude(difference);
            }
        }

        onChangesComputed?.Invoke(all);

        if (applied.Count == 0)
        {
            return new ApplyResult(true, applied, excluded, messages);
        }

        var publish = result.PublishChangesToDatabase(cancellationToken);
        if (!publish.Success)
        {
            messages.AddRange(publish.Errors.Select(e =>
                new RawMessage(e.MessageType.ToString(), $"{e.Prefix}{e.Number}", e.Message)));
        }

        return new ApplyResult(publish.Success, applied, excluded, messages);
    }

    // ------------------------------------------------------------------ programmables

    private static readonly ModelTypeClass[] ProgrammableTypeClasses =
    {
        Procedure.TypeClass, View.TypeClass, ScalarFunction.TypeClass,
        TableValuedFunction.TypeClass, DmlTrigger.TypeClass,
    };

    private static ProgrammableAnalysis AnalyzeProgrammables(string desiredStateDirectory)
    {
        // The loader classifies out deploy scripts / seed DML; its skip warnings are
        // deliberately NOT included here — they surface once via the compare path,
        // and both paths share the loader so the file sets always agree.
        var loaded = DesiredStateLoader.Load(desiredStateDirectory);
        if (loaded.Errors.Count > 0)
        {
            return new ProgrammableAnalysis(Array.Empty<ProgrammableObjectInfo>(), loaded.Errors);
        }

        // Built with per-batch source names so each object knows its defining file.
        // The batch suffix exists because AddOrUpdateObjects REPLACES everything
        // previously registered under the same source name.
        using var model = new TSqlModel(ModelVersion, new TSqlModelOptions());
        foreach (var file in loaded.ModelFiles)
        {
            var batchIndex = 0;
            foreach (var batch in SqlBatchSplitter.Split(file.Text))
            {
                model.AddOrUpdateObjects(NormalizeToCreate(batch), $"{file.Path}::{batchIndex++}", new TSqlObjectOptions());
            }
        }

        var messages = model.Validate()
            .Where(m => m.MessageType == DacMessageType.Error)
            .Select(m => new RawMessage("Error", $"{m.Prefix}{m.Number}", m.Message))
            .ToList();
        if (messages.Count > 0)
        {
            return new ProgrammableAnalysis(Array.Empty<ProgrammableObjectInfo>(), messages);
        }

        var programmables = model.GetObjects(DacQueryScopes.UserDefined, ProgrammableTypeClasses).ToList();
        var names = programmables.Select(FullName).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var objects = new List<ProgrammableObjectInfo>();
        foreach (var obj in programmables)
        {
            var name = FullName(obj);
            var file = SourceFileOf(obj);
            if (file is null)
            {
                messages.Add(new RawMessage("Error", "SCHEMORPH003",
                    $"Programmable object {name} has no source file in the desired state."));
                continue;
            }

            var dependsOn = obj.GetReferenced(DacQueryScopes.UserDefined)
                .Select(FullName)
                .Where(r => names.Contains(r) && !r.Equals(name, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(r => r, StringComparer.OrdinalIgnoreCase)
                .ToList();

            objects.Add(new ProgrammableObjectInfo(
                name, obj.ObjectType.Name, file, RewriteToCreateOrAlter(File.ReadAllText(file)), dependsOn));
        }

        // ADR-0002: one programmable object per file, with a clear error otherwise —
        // re-applying a shared file per object would redefine its siblings too.
        foreach (var group in objects.GroupBy(o => o.FilePath, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
        {
            messages.Add(new RawMessage("Error", "SCHEMORPH004",
                $"One programmable object per file: {group.Key} defines " +
                string.Join(", ", group.Select(o => o.ObjectName).Order(StringComparer.OrdinalIgnoreCase)) + "."));
        }

        return new ProgrammableAnalysis(objects, messages);
    }

    /// <summary>
    /// Idempotent-redefinition rewrite. "CREATE OR ALTER" never matches (OR follows
    /// CREATE), so already-idempotent files pass through unchanged.
    /// </summary>
    private static string RewriteToCreateOrAlter(string sql) =>
        System.Text.RegularExpressions.Regex.Replace(
            sql, @"\bCREATE\s+(PROCEDURE|PROC|FUNCTION|VIEW|TRIGGER)\b",
            "CREATE OR ALTER $1",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase, TimeSpan.FromSeconds(5));

    /// <summary>
    /// The TSqlModel only accepts plain CREATE syntax; users may still write
    /// CREATE OR ALTER in desired-state files (it is what we execute anyway).
    /// Internal: LiveDefinitionMatcher shares this equivalence.
    /// </summary>
    internal static string NormalizeToCreate(string sql) =>
        System.Text.RegularExpressions.Regex.Replace(
            sql, @"\bCREATE\s+OR\s+ALTER\b", "CREATE",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase, TimeSpan.FromSeconds(5));

    private static string? SourceFileOf(TSqlObject obj)
    {
        var sourceName = obj.GetSourceInformation()?.SourceName;
        if (sourceName is null) return null;
        var suffix = sourceName.LastIndexOf("::", StringComparison.Ordinal);
        return suffix < 0 ? sourceName : sourceName[..suffix];
    }

    private static string FullName(TSqlObject obj) => string.Join(".", obj.Name.Parts);

    // ------------------------------------------------------------------ inspect

    private static InspectResult Inspect(InspectRequest request, CancellationToken cancellationToken)
    {
        var dacpacPath = Path.Combine(Path.GetTempPath(), $"schemorph-inspect-{Guid.NewGuid():N}.dacpac");
        try
        {
            var databaseName = new SqlConnectionStringBuilder(request.ConnectionString).InitialCatalog;
            new DacServices(request.ConnectionString).Extract(
                dacpacPath, databaseName, "Schemorph", new Version(0, 0, 1),
                cancellationToken: cancellationToken);

            using var model = new TSqlModel(dacpacPath, DacSchemaModelStorageType.Memory);
            return new InspectResult(WriteDesiredState(model, request.OutputDirectory));
        }
        finally
        {
            File.Delete(dacpacPath);
        }
    }

    private static IReadOnlyList<string> WriteDesiredState(TSqlModel model, string outputDirectory)
    {
        // Conventional layout (architecture.md): one file per object, grouped by kind.
        var kinds = new (ModelTypeClass Type, string Directory)[]
        {
            (Table.TypeClass, "tables"),
            (View.TypeClass, "views"),
            (Procedure.TypeClass, "procedures"),
            (ScalarFunction.TypeClass, "functions"),
            (TableValuedFunction.TypeClass, "functions"),
            (DmlTrigger.TypeClass, "triggers"),
        };

        // Constraints and indexes are separate top-level elements in the DacFx
        // model even when declared inline; fold them into their table's file so
        // each file is a complete, self-applicable desired state.
        var attachments = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var attachmentTypes = new[]
        {
            PrimaryKeyConstraint.TypeClass, ForeignKeyConstraint.TypeClass,
            UniqueConstraint.TypeClass, CheckConstraint.TypeClass,
            DefaultConstraint.TypeClass, Index.TypeClass,
        };
        foreach (var obj in model.GetObjects(DacQueryScopes.UserDefined, attachmentTypes))
        {
            if (!obj.TryGetScript(out var script)) continue;
            var table = obj.GetReferenced(DacQueryScopes.UserDefined)
                .FirstOrDefault(r => r.ObjectType == Table.TypeClass);
            if (table is null) continue;
            var key = string.Join(".", table.Name.Parts);
            (attachments.TryGetValue(key, out var list) ? list : attachments[key] = new List<string>()).Add(script);
        }

        var written = new List<string>();
        foreach (var (type, directory) in kinds)
        {
            foreach (var obj in model.GetObjects(DacQueryScopes.UserDefined, type))
            {
                if (!obj.TryGetScript(out var script)) continue;

                var fullName = string.Join(".", obj.Name.Parts);
                if (Schemorph.Core.Ledger.LedgerObjects.IsLedgerObject(fullName)) continue;   // self-exclusion
                var content = new StringBuilder().AppendLine(script.Trim()).AppendLine("GO");
                if (type == Table.TypeClass && attachments.TryGetValue(fullName, out var extras))
                {
                    foreach (var extra in extras)
                    {
                        content.AppendLine(extra.Trim()).AppendLine("GO");
                    }
                }

                var dir = Path.Combine(outputDirectory, directory);
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, $"{fullName}.sql");
                File.WriteAllText(path, content.ToString());
                written.Add(path);
            }
        }

        return written;
    }

    // ------------------------------------------------------------------ shared

    /// <summary>Model build + dacpac + SchemaComparison lifecycle for compare/apply.</summary>
    private sealed class ComparisonSession : IDisposable
    {
        public List<RawMessage> ModelErrors { get; private init; } = new();
        public List<RawMessage> LoadWarnings { get; private init; } = new();
        public SchemaComparisonResult? Result { get; private init; }
        private string? _dacpacPath;

        public static ComparisonSession Open(string desiredStateDirectory, string connectionString, CancellationToken cancellationToken)
        {
            // Non-model files (deploy scripts, seed DML) are classified out here,
            // loudly: the skip warnings ride on this session and reach the plan.
            var loaded = DesiredStateLoader.Load(desiredStateDirectory);
            if (loaded.Errors.Count > 0)
            {
                return new ComparisonSession { ModelErrors = loaded.Errors.ToList() };
            }

            using var model = new TSqlModel(ModelVersion, new TSqlModelOptions());
            foreach (var file in loaded.ModelFiles)
            {
                // TODO(core): replace line-based GO splitting with ScriptDom batch parsing.
                foreach (var batch in SqlBatchSplitter.Split(file.Text))
                {
                    model.AddObjects(NormalizeToCreate(batch));
                }
            }

            var errors = model.Validate()
                .Where(m => m.MessageType == DacMessageType.Error)
                .Select(m => new RawMessage("Error", $"{m.Prefix}{m.Number}", m.Message))
                .ToList();
            if (errors.Count > 0)
            {
                return new ComparisonSession { ModelErrors = errors };
            }

            var dacpacPath = Path.Combine(Path.GetTempPath(), $"schemorph-{Guid.NewGuid():N}.dacpac");
            DacPackageExtensions.BuildPackage(dacpacPath, model,
                new PackageMetadata { Name = "SchemorphDesiredState", Version = "0.0.1" });

            var comparison = new SchemaComparison(
                new SchemaCompareDacpacEndpoint(dacpacPath),
                new SchemaCompareDatabaseEndpoint(connectionString));

            // Report the full delta; inclusion policy is injected by the core.
            comparison.Options.BlockOnPossibleDataLoss = false;
            comparison.Options.DropObjectsNotInSource = true;
            // ADR-0004: a mid-stream publish failure must leave the schema untouched.
            comparison.Options.IncludeTransactionalScripts = true;

            return new ComparisonSession
            {
                Result = comparison.Compare(cancellationToken),
                LoadWarnings = loaded.Warnings.ToList(),
                _dacpacPath = dacpacPath,
            };
        }

        public void Dispose()
        {
            if (_dacpacPath is not null) File.Delete(_dacpacPath);
        }
    }

    private static List<RawMessage> CollectMessages(SchemaComparisonResult result) =>
        result.GetErrors()
            .Select(e => new RawMessage(e.MessageType.ToString(), $"{e.Prefix}{e.Number}", e.Message))
            .ToList();

    private static RawChange ToRawChange(SchemaDifference difference) => new(
        difference.UpdateAction.ToString(),
        (difference.SourceObject ?? difference.TargetObject)?.ObjectType.Name ?? "Unknown",
        FullName(difference));

    private static string FullName(SchemaDifference difference)
    {
        var parts = (difference.SourceObject ?? difference.TargetObject)?.Name.Parts;
        return parts is { Count: > 0 } ? string.Join(".", parts) : difference.Name ?? "(unnamed)";
    }
}
