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

    public Task<IDesiredState> LoadDesiredStateAsync(string desiredStateDirectory, CancellationToken cancellationToken = default)
        => Task.Run<IDesiredState>(() => SqlServerDesiredState.Load(desiredStateDirectory), cancellationToken);

    public Task<CompareResult> CompareAsync(CompareRequest request, CancellationToken cancellationToken = default)
        => Task.Run(() => Compare(request, cancellationToken), cancellationToken);

    public Task<ApplyResult> ApplyAsync(ApplyRequest request, Func<RawChange, bool> includeChange, Action<CompareResult>? onChangesComputed = null, CancellationToken cancellationToken = default)
        => Task.Run(() => Apply(request, includeChange, onChangesComputed, cancellationToken), cancellationToken);

    public Task<ProgrammableAnalysis> AnalyzeProgrammablesAsync(IDesiredState desiredState, CancellationToken cancellationToken = default)
        => Task.Run(() => AnalyzeProgrammables(SqlServerDesiredState.From(desiredState)), cancellationToken);

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
                        && LiveDefinitionMatcher.Matches(o.FileText, definition))
            .ToList();
    }

    public Task<IReadOnlyList<MigrationLintSignal>> LintMigrationScriptAsync(
        string scriptText, CancellationToken cancellationToken = default)
        => Task.FromResult(MigrationScriptLinter.Lint(scriptText));

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
        var state = SqlServerDesiredState.From(request.DesiredState);
        using var session = ComparisonSession.Open(state, request.ConnectionString, cancellationToken);
        if (session.ModelErrors.Count > 0)
        {
            return new CompareResult(Array.Empty<RawChange>(), session.ModelErrors, UpdateScript: null);
        }

        var result = session.Result!;
        var messages = CollectMessages(result);
        if (messages.Any(m => m.Severity == nameof(DacMessageType.Error)))
        {
            // No changes, and no basis for saying so. Callers fail on the errors
            // rather than treat this as an in-sync database (DiffOperation,
            // ApplyOperation) — an unreadable target never becomes a plan.
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

        return new CompareResult(changes, messages, script,
            script is null ? null : UpdateScriptAttributor.Attribute(script, changes),
            TablesWithColumnChanges(result.Differences));
    }

    // ------------------------------------------------------------------ apply

    private static ApplyResult Apply(ApplyRequest request, Func<RawChange, bool> includeChange, Action<CompareResult>? onChangesComputed, CancellationToken cancellationToken)
    {
        var state = SqlServerDesiredState.From(request.DesiredState);
        using var session = ComparisonSession.Open(state, request.ConnectionString, cancellationToken);
        if (session.ModelErrors.Count > 0)
        {
            return new ApplyResult(false, Array.Empty<RawChange>(), Array.Empty<RawChange>(), session.ModelErrors);
        }

        var result = session.Result!;
        var messages = CollectMessages(result);
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

        onChangesComputed?.Invoke(new CompareResult(all, Array.Empty<RawMessage>(), UpdateScript: null,
            ChangeScripts: null, TablesWithColumnChanges(result.Differences)));

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

    // Resolved on demand, never captured at type-load: the DacFx model type-class
    // statics (Procedure.TypeClass, ...) are only populated once DacFx's model
    // services have initialized (the first TSqlModel build). Baking them into a
    // static field lets an earlier touch of any SqlServerProvider static member
    // freeze them as null, which then NREs every GetObjects for the process.
    private static ModelTypeClass[] ProgrammableTypeClasses =>
    [
        Procedure.TypeClass, View.TypeClass, ScalarFunction.TypeClass,
        TableValuedFunction.TypeClass, DmlTrigger.TypeClass,
    ];

    private static ProgrammableAnalysis AnalyzeProgrammables(SqlServerDesiredState state)
    {
        // Built with per-batch source names so each object knows its defining file.
        // The batch suffix exists because AddOrUpdateObjects REPLACES everything
        // previously registered under the same source name.
        using var model = new TSqlModel(ModelVersion, new TSqlModelOptions());
        foreach (var file in state.ModelFiles)
        {
            var batchIndex = 0;
            foreach (var batch in file.Batches)
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
        var textByPath = state.ModelFiles.ToDictionary(f => f.Path, f => f.Text, StringComparer.OrdinalIgnoreCase);

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

            var referenced = obj.GetReferenced(DacQueryScopes.UserDefined).ToList();
            var dependsOn = referenced
                .Select(FullName)
                .Where(r => names.Contains(r) && !r.Equals(name, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(r => r, StringComparer.OrdinalIgnoreCase)
                .ToList();

            // A referenced column names its table in its first two parts; a
            // reference to the table itself (SELECT *) names it directly.
            var dependsOnTables = referenced
                .Where(r => r.ObjectType == Table.TypeClass || r.ObjectType == Column.TypeClass)
                .Select(r => r.ObjectType == Table.TypeClass
                    ? FullName(r)
                    : string.Join(".", r.Name.Parts.Take(2)))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(r => r, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var fileText = textByPath[file];
            objects.Add(new ProgrammableObjectInfo(
                name, obj.ObjectType.Name, file, fileText, RewriteToCreateOrAlter(fileText),
                dependsOn, dependsOnTables));
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
            return new InspectResult(RenderDesiredState(model));
        }
        finally
        {
            File.Delete(dacpacPath);
        }
    }

    private static IReadOnlyList<DesiredStateFile> RenderDesiredState(TSqlModel model)
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

        var rendered = new List<DesiredStateFile>();
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

                rendered.Add(new DesiredStateFile($"{directory}/{fullName}.sql", content.ToString()));
            }
        }

        return rendered;
    }

    // ------------------------------------------------------------------ shared

    /// <summary>
    /// Security principals (users, logins, roles, role membership, permissions)
    /// are excluded from the SQL Server declarative diff — the open per-provider
    /// edge-case classification of design principle §2, settled here (ADR-0006).
    /// They are not the structural state the declarative strategy owns; a code-gen
    /// desired-state never emits them; and dropping a login because it is "absent
    /// from the desired state" is exactly the silent destruction §4 forbids
    /// (DacFx reported <c>DROP USER [app-login]</c> as a non-destructive change,
    /// slipping past the destructive gate). Managing principals declaratively is a
    /// future opt-in, added when a consumer actually needs it — not a default.
    /// </summary>
    private static readonly ObjectType[] ExcludedSecurityPrincipalTypes =
    {
        ObjectType.Users, ObjectType.Logins, ObjectType.LinkedServerLogins,
        ObjectType.DatabaseRoles, ObjectType.ApplicationRoles, ObjectType.ServerRoles,
        ObjectType.RoleMembership, ObjectType.ServerRoleMembership, ObjectType.Permissions,
    };

    /// <summary>
    /// The comparison policy shared by compare and apply. Reports the full
    /// structural delta (inclusion policy is injected by the core) minus the
    /// object classes Schemorph deliberately does not manage declaratively.
    /// </summary>
    internal static void ConfigureCompareOptions(DacDeployOptions options)
    {
        options.BlockOnPossibleDataLoss = false;
        options.DropObjectsNotInSource = true;
        // ADR-0004: a mid-stream publish failure must leave the schema untouched.
        options.IncludeTransactionalScripts = true;
        // Column order is not meaningful state for a declarative SQL-first model:
        // a code-generated CREATE TABLE lists columns in logical order, which
        // rarely matches a live table's physical ordinal after columns were added
        // over time. Honoring the difference makes DacFx rebuild the whole table
        // (new table, copy rows, drop, rename) just to re-seat a column — turning
        // an additive `ALTER TABLE ADD` into full data motion on every unrelated
        // table. Ignoring it keeps additive changes in place (§4: don't rebuild a
        // data-holding table for a cosmetic reorder).
        options.IgnoreColumnOrder = true;
        // Security principals are out of the declarative model by default (ADR-0006).
        options.ExcludeObjectTypes = (options.ExcludeObjectTypes ?? Array.Empty<ObjectType>())
            .Concat(ExcludedSecurityPrincipalTypes)
            .Distinct()
            .ToArray();
    }

    /// <summary>Model build + dacpac + SchemaComparison lifecycle for compare/apply.</summary>
    private sealed class ComparisonSession : IDisposable
    {
        public List<RawMessage> ModelErrors { get; private init; } = new();
        public SchemaComparisonResult? Result { get; private init; }
        private string? _dacpacPath;

        public static ComparisonSession Open(SqlServerDesiredState state, string connectionString, CancellationToken cancellationToken)
        {
            using var model = new TSqlModel(ModelVersion, new TSqlModelOptions());
            foreach (var file in state.ModelFiles)
            {
                foreach (var batch in file.Batches)
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

            ConfigureCompareOptions(comparison.Options);

            return new ComparisonSession
            {
                Result = comparison.Compare(cancellationToken),
                _dacpacPath = dacpacPath,
            };
        }

        public void Dispose()
        {
            if (_dacpacPath is not null) File.Delete(_dacpacPath);
        }
    }

    private static List<RawMessage> CollectMessages(SchemaComparisonResult result)
    {
        var messages = result.GetErrors()
            .Select(e => new RawMessage(e.MessageType.ToString(), $"{e.Prefix}{e.Number}", e.Message))
            .ToList();
        if (RestrictedComparisonWarning(messages) is { } restricted)
        {
            messages.Add(restricted);
        }
        return messages;
    }

    /// <summary>
    /// An incomplete comparison must never read as an in-sync database: when the
    /// target could not be read in full, changes to what was missed are simply
    /// absent, so "no differences" means nothing. This surfaces that as an
    /// explicit warning.
    ///
    /// Keyed on the *effect* — a comparison that reported an error — rather than
    /// on any particular missing permission, because measurement (cycle 66)
    /// showed permissions do not predict completeness in either direction:
    ///
    ///   login                                  server VAD / db VD / obj VD   changes
    ///   db_owner, no server-scope grant                 0 / 1 / 1            complete
    ///   db_datareader + DENY VIEW DEFINITION            0 / 0 / 0            EMPTY
    ///   db-scope GRANT + object-level DENY              0 / 1 / 0            EMPTY
    ///
    /// The first row is the shape this tool's own docs recommend, and it reads
    /// everything — so firing on "lacks VIEW ANY DEFINITION" is a false positive
    /// on the common least-privilege setup. The third row is why checking the
    /// database-scoped permission instead would be worse: it looks granted while
    /// the comparison still comes back empty, trading a false positive for a
    /// false negative in a safety warning.
    ///
    /// What does separate them is that DacFx reports an error ("the reverse
    /// engineering operation cannot continue…") in exactly the incomplete cases,
    /// while the complete one carries only the benign server-scope warning. Note
    /// that warning is doubly inapplicable here: it restricts the comparison "to
    /// database scoped elements if the source is a database", and this provider's
    /// source is always a dacpac.
    ///
    /// Source-model errors never reach this point — <see cref="ComparisonSession.Open"/>
    /// returns them separately — so an error here is about reading the target.
    /// </summary>
    internal static RawMessage? RestrictedComparisonWarning(IReadOnlyList<RawMessage> messages)
    {
        var errors = messages.Where(m => m.Severity == "Error").ToList();
        if (errors.Count == 0) return null;

        // Echo what the engine actually said instead of asserting a cause: the
        // reason ("no permission on the database" vs "denied on at least one
        // object") decides what the operator has to fix.
        var reported = string.Join(" ", errors.Select(e => e.Text));
        return new RawMessage("Warning", "SCHEMORPH008",
            "The comparison could not read the target completely, so changes to what it missed are absent " +
            "from this plan — a partial or empty result must not be read as \"in sync\". Reported: " + reported);
    }

    /// <summary>
    /// Tables where an existing column's definition changes. DacFx reports this
    /// as a Change difference on the table with a Change difference on the column
    /// beneath it — the shape a retype, a nullability flip or a collation change
    /// all take. Column additions and removals are deliberately not included:
    /// they leave an explicitly-projected dependent object's meaning intact, and
    /// treating every additive column as an invalidation would redefine the world
    /// on the most common change there is.
    /// </summary>
    private static IReadOnlyList<string> TablesWithColumnChanges(IEnumerable<SchemaDifference> differences) =>
        differences
            .Where(d => d.UpdateAction == SchemaUpdateAction.Change
                        && (d.SourceObject ?? d.TargetObject)?.ObjectType == Table.TypeClass
                        && d.Children.Any(c => c.UpdateAction == SchemaUpdateAction.Change
                                               && (c.SourceObject ?? c.TargetObject)?.ObjectType == Column.TypeClass))
            .Select(FullName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
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
