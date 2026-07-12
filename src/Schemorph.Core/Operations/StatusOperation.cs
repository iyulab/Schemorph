using Schemorph.Core.Ledger;
using Schemorph.Core.Migrations;
using Schemorph.Core.Planning;
using Schemorph.Core.Providers;
using Schemorph.Core.Redefine;

namespace Schemorph.Core.Operations;

/// <summary>
/// The status orchestration: has the database diverged from the desired state,
/// what has Schemorph done here, what is waiting to run? Entirely read-only —
/// it reuses the diff (drift IS the pending plan) and the ledger/migration
/// judgments the apply path uses, so status can never disagree with what apply
/// would actually do.
/// </summary>
public static class StatusOperation
{
    public sealed record Request(
        string SchemaDir,
        string ConnectionString,
        string? MigrationsDir = null);

    public sealed record LedgerSummary(
        int TotalEntries,
        int Failures,
        IReadOnlyDictionary<string, int> ByKind,
        DateTime? LastActivityUtc);

    public sealed record MigrationsSummary(
        IReadOnlyList<string> PendingFiles,
        int AppliedCount,
        IReadOnlyList<string> IgnoredFiles);

    public sealed record Status(
        Plan Plan,
        LedgerSummary Ledger,
        MigrationsSummary? Migrations)
    {
        /// <summary>Anything to do — drift, or migrations waiting to run.</summary>
        public bool HasPendingWork => Plan.HasChanges || Migrations is { PendingFiles.Count: > 0 };
    }

    public sealed record StatusResult(
        Status? Status, IReadOnlyList<RawMessage> Errors, DiffOperation.FailureStage Stage)
    {
        public bool Success => Status is not null;
    }

    private static readonly string[] LedgerKinds =
        { "declarative", RedefineRunner.LedgerKind, MigrationRunner.LedgerKind };

    public static async Task<StatusResult> RunAsync(
        IDatabaseProvider provider, ILedgerStore ledger, Request request,
        CancellationToken cancellationToken = default)
    {
        // Drift = the plan that a diff would produce right now.
        var diff = await DiffOperation.RunAsync(
            provider, ledger, request.SchemaDir, request.ConnectionString,
            allowDestructive: true,   // status reports ALL drift, gated or not
            cancellationToken);
        if (!diff.Success)
        {
            return new StatusResult(null, diff.Errors, diff.Stage);
        }

        var byKind = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var total = 0;
        var failures = 0;
        DateTime? lastActivity = null;
        foreach (var kind in LedgerKinds)
        {
            var entries = await ledger.ReadAsync(request.ConnectionString, kind, cancellationToken);
            byKind[kind] = entries.Count;
            total += entries.Count;
            failures += entries.Count(e => !e.Succeeded);
            var last = entries.LastOrDefault()?.AppliedAtUtc;
            if (last is not null && (lastActivity is null || last > lastActivity)) lastActivity = last;
        }

        MigrationsSummary? migrations = null;
        if (request.MigrationsDir is { } dir)
        {
            var plan = await new MigrationRunner(provider, ledger).PlanAsync(dir, request.ConnectionString, cancellationToken);
            migrations = new MigrationsSummary(
                plan.Pending.Select(s => s.FileName).ToList(), plan.AppliedCount, plan.IgnoredFiles);
        }

        return new StatusResult(
            new Status(diff.Plan!, new LedgerSummary(total, failures, byKind, lastActivity), migrations),
            Array.Empty<RawMessage>(), DiffOperation.FailureStage.None);
    }
}
