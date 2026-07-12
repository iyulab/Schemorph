using Schemorph.Core.Ledger;
using Schemorph.Core.Migrations;
using Schemorph.Core.Planning;
using Schemorph.Core.Providers;
using Schemorph.Core.Redefine;

namespace Schemorph.Core.Operations;

/// <summary>
/// The apply orchestration, owned by the core so the CLI verb and the MCP tool
/// render one operation. Order is the strategy order (ADR-0002): declarative
/// publish → redefines → migrations, all recorded in the ledger (ADR-0004).
///
/// The apply gate lives here: with an expected fingerprint, the plan computed in
/// the SAME comparison session that would execute is checked first — a mismatch
/// aborts before anything runs. The redefine phase executes the plan that was
/// fingerprinted, never a silent re-plan.
/// </summary>
public static class ApplyOperation
{
    public sealed record Request(
        string SchemaDir,
        string ConnectionString,
        bool AllowDestructive = false,
        string? MigrationsDir = null,
        string? ExpectedPlanHash = null);

    public enum FailureStage { None, DesiredState, PlanMismatch, Publish }

    public sealed record Outcome(
        bool Success,
        FailureStage Stage,
        IReadOnlyList<RawMessage> Errors,
        Plan? Plan,
        IReadOnlyList<RawChange> Applied,
        IReadOnlyList<RawChange> ExcludedVisible,
        IReadOnlyList<RawMessage> VisibleMessages,
        RedefineRunResult? Redefines,
        MigrationRunResult? Migrations);

    /// <param name="onPlan">
    /// Fires with the plan after the gate passes, before anything executes —
    /// surfaces render their preview here.
    /// </param>
    public static async Task<Outcome> RunAsync(
        IDatabaseProvider provider, ILedgerStore ledger, Request request,
        Action<Plan>? onPlan = null, CancellationToken cancellationToken = default)
    {
        // One load serves analysis and apply — the desired state is read and
        // classified exactly once per operation, and a broken file fails here,
        // before any DB work.
        var state = await provider.LoadDesiredStateAsync(request.SchemaDir, cancellationToken);
        if (state.Errors.Count > 0)
        {
            return Failure(FailureStage.DesiredState, state.Errors);
        }

        // Strategy 2 analysis runs next, still ahead of any DB work.
        var programmables = await provider.AnalyzeProgrammablesAsync(state, cancellationToken);
        if (programmables.Messages.Any(m => m.Severity == "Error"))
        {
            return Failure(FailureStage.DesiredState, programmables.Messages);
        }

        // The ledger exists before anything runs, so failures are recordable too
        // (ADR-0004). The table is self-excluded from comparison, so creating it
        // here never perturbs the plan.
        await ledger.EnsureInitializedAsync(request.ConnectionString, cancellationToken);

        var redefineRunner = new RedefineRunner(provider, ledger);
        var redefinePlan = await redefineRunner.PlanAsync(programmables, request.ConnectionString, cancellationToken);

        // The plan is announced from the SAME comparison session that applies
        // (provider hook), so what is gated and shown is exactly what runs.
        Plan? plan = null;
        ApplyResult result;
        try
        {
            result = await provider.ApplyAsync(
                new ApplyRequest(state, request.ConnectionString),
                change => PlanBuilder.ShouldInclude(change, request.AllowDestructive),
                changes =>
                {
                    plan = PlanBuilder.Build(
                        new CompareResult(changes, Array.Empty<RawMessage>(), UpdateScript: null),
                        request.AllowDestructive, redefinePlan.Pending.Select(p => p.ToPlanAction()).ToList());
                    if (request.ExpectedPlanHash is { } expected)
                    {
                        var actual = PlanFingerprint.Compute(plan);
                        if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
                        {
                            throw new PlanMismatchException(expected, actual);
                        }
                    }
                    onPlan?.Invoke(plan);
                },
                cancellationToken);
        }
        catch (PlanMismatchException ex)
        {
            return Failure(FailureStage.PlanMismatch,
                new[] { new RawMessage("Error", "plan_mismatch", ex.Message) }) with { Plan = plan };
        }

        // Classification skip warnings surface once per operation, ahead of the
        // provider's own messages (they used to ride the comparison session).
        var messages = state.Warnings.Concat(result.Messages).ToList();

        if (!result.Success)
        {
            var errorText = string.Join("; ", messages
                .Where(m => m.Severity == "Error").Select(m => $"{m.Code}: {m.Text}"));
            await ledger.AppendFailureBestEffortAsync(request.ConnectionString, new LedgerEntry(
                "declarative", "(publish)", "Publish", Checksum: null,
                Succeeded: false, Detail: errorText), cancellationToken);
            return Failure(FailureStage.Publish, messages) with { Plan = plan };
        }

        // Every applied change is recorded in the history ledger — the audit trail.
        await ledger.AppendAsync(request.ConnectionString, result.AppliedChanges
            .Select(c => new LedgerEntry("declarative", c.ObjectName, c.Operation, Checksum: null,
                Succeeded: true, Detail: c.ObjectType))
            .ToList(), cancellationToken);

        // Strategy 2: idempotent re-definitions run after the declarative publish
        // (structural prerequisites first). Declarative drops leave a tombstone so
        // re-adding an identical file later still re-creates the object. Executes
        // the SAME redefine plan that was fingerprinted above.
        await redefineRunner.RecordDropsAsync(request.ConnectionString, result.AppliedChanges, cancellationToken);
        var redefineRun = await redefineRunner.RunAsync(programmables, redefinePlan, request.ConnectionString, cancellationToken);

        // Strategy 3: versioned migrations run after the declarative apply.
        MigrationRunResult? migrationRun = null;
        if (request.MigrationsDir is { } migrationsDir)
        {
            migrationRun = await new MigrationRunner(provider, ledger).RunAsync(migrationsDir, request.ConnectionString, cancellationToken);
        }

        // Schemorph's own bookkeeping stays invisible in user-facing output, and
        // redefine-routed exclusions are not "excluded" — they show as redefinitions.
        var excludedVisible = result.ExcludedChanges
            .Where(c => !LedgerObjects.IsLedgerObject(c.ObjectName) && !PlanBuilder.RoutesToRedefine(c))
            .ToList();
        var visibleMessages = messages
            .Where(m => !LedgerObjects.IsLedgerObject(m.Text))
            .Select(m => m with { Text = Redaction.Redact(m.Text) })
            .ToList();

        return new Outcome(true, FailureStage.None, Array.Empty<RawMessage>(),
            plan, result.AppliedChanges, excludedVisible, visibleMessages, redefineRun, migrationRun);
    }

    private static Outcome Failure(FailureStage stage, IReadOnlyList<RawMessage> errors) =>
        new(false, stage, errors, null, Array.Empty<RawChange>(), Array.Empty<RawChange>(),
            Array.Empty<RawMessage>(), null, null);
}
