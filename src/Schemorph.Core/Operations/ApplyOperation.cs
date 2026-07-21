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

    /// <summary>
    /// Where an apply stopped. The strategy order (ADR-0002) is also the order of
    /// this enum, so the stage says what came before it — and everything before it
    /// committed. There is no cross-stage rollback (ADR-0004), which is exactly why
    /// the stage has to be reported rather than folded into a generic failure.
    /// </summary>
    public enum FailureStage { None, DesiredState, PlanMismatch, Publish, Redefine, Migration }

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
        // The base plan is what the files alone imply; the comparison can add to
        // it (a column change invalidates what depends on that column), so the
        // final plan is only known inside the hook — and that is the plan that is
        // fingerprinted, shown, and executed.
        var basePlan = await redefineRunner.PlanAsync(programmables, request.ConnectionString, cancellationToken);
        var redefinePlan = basePlan;

        // The plan is announced from the SAME comparison session that applies
        // (provider hook), so what is gated and shown is exactly what runs.
        Plan? plan = null;
        ApplyResult result;
        try
        {
            result = await provider.ApplyAsync(
                new ApplyRequest(state, request.ConnectionString),
                change => PlanBuilder.ShouldInclude(change, request.AllowDestructive),
                computed =>
                {
                    redefinePlan = RedefineRunner.WithInvalidations(
                        basePlan, programmables, computed.TablesWithColumnChanges);
                    plan = PlanBuilder.Build(
                        computed, request.AllowDestructive,
                        redefinePlan.Pending.Select(p => p.ToPlanAction()).ToList());
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

        // From here on the declarative changes are committed and there is no
        // rollback across stages, so an execution failure below is reported WITH
        // what it left behind — never as a bare error that implies nothing ran.
        // Only execution failures are caught: RedefineException (dependency cycle)
        // and MigrationException (duplicate version / edited migration) describe an
        // invalid desired state, are raised before their stage executes anything,
        // and keep propagating to the invalid_state mapping they always had.
        RedefineRunResult redefineRun;
        try
        {
            redefineRun = await redefineRunner.RunAsync(programmables, redefinePlan, request.ConnectionString, cancellationToken);
        }
        catch (RedefineExecutionException ex)
        {
            return Failure(FailureStage.Redefine,
                new[] { new RawMessage("Error", "redefine_execution_failed", ex.Message) }) with
            {
                Plan = plan,
                Applied = result.AppliedChanges,
                Redefines = new RedefineRunResult(ex.Redefined, 0, Array.Empty<string>()),
            };
        }

        // Strategy 3: versioned migrations run after the declarative apply.
        MigrationRunResult? migrationRun = null;
        if (request.MigrationsDir is { } migrationsDir)
        {
            try
            {
                migrationRun = await new MigrationRunner(provider, ledger).RunAsync(migrationsDir, request.ConnectionString, cancellationToken);
            }
            catch (MigrationExecutionException ex)
            {
                return Failure(FailureStage.Migration,
                    new[] { new RawMessage("Error", "migration_execution_failed", ex.Message) }) with
                {
                    Plan = plan,
                    Applied = result.AppliedChanges,
                    Redefines = redefineRun,
                    Migrations = new MigrationRunResult(
                        ex.Applied, 0, Array.Empty<string>(), Array.Empty<RawMessage>()),
                };
            }
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
