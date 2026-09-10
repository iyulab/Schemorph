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
    ///
    /// <see cref="Commit"/> is the exception: it names a failure AFTER every
    /// stage above it already ran clean. Under Transactional atomicity nothing
    /// is durable until the session's own commit succeeds, so a commit that
    /// throws (e.g. the connection drops between the last statement and the
    /// acknowledgement) leaves the caller unable to tell whether the database
    /// actually persisted the work — it must be told the attempt, not handed a
    /// false success.
    /// </summary>
    public enum FailureStage { None, DesiredState, PlanMismatch, Publish, Redefine, Migration, Commit }

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
        // Dialect knowledge only a connection can supply — see DiffOperation's
        // identical call. Ahead of the apply gate below, same as the analysis
        // it refines: what is fingerprinted and gated is what this call decided.
        programmables = await provider.RefineProgrammablesAsync(programmables, state, request.ConnectionString, cancellationToken);
        if (programmables.Messages.Any(m => m.Severity == "Error"))
        {
            return Failure(FailureStage.DesiredState, programmables.Messages);
        }

        // The ledger is initialized AFTER the comparison, not before — see the note
        // below. A missing ledger reads as "no history" (like diff), so the redefine
        // plan is computed the same way in both operations.
        var redefineRunner = new RedefineRunner(provider, ledger);
        // The base plan is what the files alone imply; the comparison can add to
        // it (a column change invalidates what depends on that column), so the
        // final plan is only known inside the hook — and that is the plan that is
        // fingerprinted, shown, and executed.
        var basePlan = await redefineRunner.PlanAsync(programmables, request.ConnectionString, cancellationToken);
        var redefinePlan = basePlan;

        // Every execution call below shares ONE session when the provider
        // promises atomicity: transactional (ADR-0004 addendum) — opened here,
        // right before the first execution call, never during the read-only
        // planning above. `await using` disposes it on every return path,
        // committed or not (a no-op if it was never opened).
        var transactional = provider.Capabilities.PlanAtomicity == ApplyAtomicity.Transactional;
        await using var session = transactional
            ? await provider.BeginApplySessionAsync(request.ConnectionString, cancellationToken)
            : null;

        // The plan is announced from the SAME comparison session that applies
        // (provider hook), so what is gated and shown is exactly what runs.
        Plan? plan = null;
        ApplyResult result;
        try
        {
            result = await provider.ApplyAsync(
                new ApplyRequest(state, request.ConnectionString),
                (change, script) => PlanBuilder.ShouldInclude(change, script, request.AllowDestructive),
                computed =>
                {
                    redefinePlan = RedefineRunner.WithInvalidations(
                        basePlan, programmables, computed.TablesWithColumnChanges);
                    plan = PlanBuilder.Build(
                        computed, request.AllowDestructive,
                        redefinePlan.Pending.Select(p => p.ToPlanAction()).ToList(),
                        provider.Capabilities.PlanAtomicity);
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
                session,
                cancellationToken);
        }
        catch (PlanMismatchException ex)
        {
            // Nothing executed yet — the gate runs inside the hook, before the
            // provider does any DDL — so a session opened above holds no
            // work. Leaving it uncommitted is enough; `await using` discards it.
            return Failure(FailureStage.PlanMismatch,
                new[] { new RawMessage("Error", "plan_mismatch", ex.Message) }) with { Plan = plan };
        }

        // The ledger is initialized HERE — after the gate has recomputed and
        // matched the plan, before anything is recorded — and it must NOT exist
        // during the comparison: a target the tool just wrote a ledger table
        // into is no longer the pristine schema diff compared, and the
        // generated update script would carry a spurious ledger DROP
        // (DropObjectsNotInSource) that trips the fingerprint gate on a plan
        // that never changed.
        //
        // The ledger TABLE'S existence must also survive independently of
        // whatever this apply does next — including a session this call's own
        // failure path is about to roll back — so it is never created through
        // the session (plan doc addendum): a session-scoped
        // EnsureInitializedAsync would vanish along with a rolled-back first
        // apply, taking the failure row below down with it (INSERT into a
        // table that no longer exists) — exactly the hazard a first apply into
        // a not-yet-existing schema hit before this call's own
        // BeginApplySessionAsync started bootstrapping the schema itself
        // (final-review Critical fix).
        await ledger.EnsureInitializedAsync(request.ConnectionString, session: null, cancellationToken);

        // Classification skip warnings surface once per operation, ahead of the
        // provider's own messages (they used to ride the comparison session).
        var messages = state.Warnings.Concat(result.Messages).ToList();

        if (!result.Success)
        {
            var errorText = string.Join("; ", messages
                .Where(m => m.Severity == "Error").Select(m => $"{m.Code}: {m.Text}"));
            var rollbackError = await TryRollbackAsync(session);
            await ledger.AppendFailureBestEffortAsync(request.ConnectionString, new LedgerEntry(
                "declarative", "(publish)", "Publish", Checksum: null,
                Succeeded: false, Detail: WithRollbackNote(errorText, rollbackError)), cancellationToken);
            return Failure(FailureStage.Publish, messages) with { Plan = plan };
        }

        // Every applied change is recorded in the history ledger — the audit
        // trail. Session-scoped: if a later stage fails and rolls back, this
        // row must not survive claiming a change that no longer happened.
        await ledger.AppendAsync(request.ConnectionString, result.AppliedChanges
            .Select(c => new LedgerEntry("declarative", c.ObjectName, c.Operation, Checksum: null,
                Succeeded: true, Detail: c.ObjectType))
            .ToList(), session, cancellationToken);

        // Strategy 2: idempotent re-definitions run after the declarative publish
        // (structural prerequisites first). Declarative drops leave a tombstone so
        // re-adding an identical file later still re-creates the object. Executes
        // the SAME redefine plan that was fingerprinted above.
        await redefineRunner.RecordDropsAsync(request.ConnectionString, result.AppliedChanges, session, cancellationToken);

        // What "committed" means below depends on the provider's declared
        // atomicity. Under Partial, the declarative changes above are already
        // committed and stay committed no matter what happens next — there is
        // no cross-stage rollback, so an execution failure below is reported
        // WITH what it left behind, never as a bare error that implies nothing
        // ran. Under Transactional, nothing below is durable yet either: it all
        // shares the ONE session this whole method threads through, so a later
        // failure rolls this back too — "committed" in that outcome only
        // describes what the caller attempted, not what the database still
        // holds. Only execution failures are caught: RedefineException
        // (dependency cycle) and MigrationException (duplicate version /
        // edited migration) describe an invalid desired state, are raised
        // before their stage executes anything, and keep propagating to the
        // invalid_state mapping they always had.
        RedefineRunResult redefineRun;
        try
        {
            redefineRun = await redefineRunner.RunAsync(programmables, redefinePlan, request.ConnectionString, session, cancellationToken);
        }
        catch (RedefineExecutionException ex)
        {
            var rollbackError = await TryRollbackAsync(session);
            return Failure(FailureStage.Redefine,
                new[] { new RawMessage("Error", "redefine_execution_failed", WithRollbackNote(ex.Message, rollbackError)) }) with
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
                migrationRun = await new MigrationRunner(provider, ledger).RunAsync(migrationsDir, request.ConnectionString, session, cancellationToken);
            }
            catch (MigrationExecutionException ex)
            {
                var rollbackError = await TryRollbackAsync(session);
                return Failure(FailureStage.Migration,
                    new[] { new RawMessage("Error", "migration_execution_failed", WithRollbackNote(ex.Message, rollbackError)) }) with
                {
                    Plan = plan,
                    Applied = result.AppliedChanges,
                    Redefines = redefineRun,
                    Migrations = new MigrationRunResult(
                        ex.Applied, 0, Array.Empty<string>(), Array.Empty<RawMessage>()),
                };
            }
        }

        if (session is not null)
        {
            try
            {
                await session.CommitAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                // Symmetric with every other stage's cleanup (final-review
                // Important #1, rollback-path version): a throw here must not
                // escape as an unhandled exception in place of a structured
                // Outcome. Best-effort rollback is attempted on the same
                // session — it may itself fail (the same broken connection
                // that likely caused the commit to throw), which is exactly
                // what TryRollbackAsync/WithRollbackNote already exist to
                // report rather than swallow.
                var rollbackError = await TryRollbackAsync(session);
                return Failure(FailureStage.Commit,
                    new[] { new RawMessage("Error", "commit_failed", WithRollbackNote(ex.Message, rollbackError)) }) with
                {
                    Plan = plan,
                    Applied = result.AppliedChanges,
                    Redefines = redefineRun,
                    Migrations = migrationRun,
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

    /// <summary>
    /// Best-effort rollback of a possibly-null session, on every cleanup path
    /// (final-review fix). Deliberately never cancellable — cleanup is already
    /// responding to a failure, often a broken connection (a likely reason the
    /// stage failed in the first place), so honoring the caller's token here
    /// too would let a rollback throw replace the structured
    /// <see cref="Failure"/> outcome (and, on the publish path, the ADR-0004
    /// failure-row write) with an unhandled exception instead. A throw is
    /// caught and its message returned rather than logged — this codebase has
    /// no logging infrastructure, so the message rides the caller's own error
    /// text via <see cref="WithRollbackNote"/> instead of being invented here.
    /// </summary>
    private static async Task<string?> TryRollbackAsync(IApplySession? session)
    {
        if (session is null) return null;
        try
        {
            await session.RollbackAsync(CancellationToken.None);
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    private static string WithRollbackNote(string text, string? rollbackError) =>
        rollbackError is null ? text : $"{text} (rollback also failed: {rollbackError})";
}
