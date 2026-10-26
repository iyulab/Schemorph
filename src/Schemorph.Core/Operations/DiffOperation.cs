using Schemorph.Core.Ledger;
using Schemorph.Core.Migrations;
using Schemorph.Core.Planning;
using Schemorph.Core.Providers;
using Schemorph.Core.Redefine;

namespace Schemorph.Core.Operations;

/// <summary>
/// The diff orchestration, owned by the core so every surface (CLI verb, MCP
/// tool) renders the same operation instead of re-wiring it (architecture.md:
/// "two renderings of the same core API"). Read-only: compares, analyzes
/// programmables, judges redefines/reconciles — never writes to the database.
/// Pending pre-stage scripts are the one thing it executes, inside a simulation the
/// provider never commits, so the plan is computed as of after them.
/// </summary>
public static class DiffOperation
{
    /// <summary>Which stage produced the errors — callers map this to their error vocabulary.</summary>
    public enum FailureStage { None, Compare, DesiredState }

    /// <param name="UpdateScript">
    /// The declarative stage's update script exactly as the provider generated it —
    /// the text a publish would execute, not a reassembly of the per-change slices
    /// the plan carries for explanation. A reviewable artifact has to be the
    /// executed artifact, so this is deliberately the raw script. Null when there is
    /// nothing to publish, or when the provider produced no script for what it
    /// planned — which diagnostic explains the latter is the provider's to report.
    /// </param>
    public sealed record DiffResult(
        Plan? Plan,
        IReadOnlyList<RawMessage> Errors,
        FailureStage Stage = FailureStage.None,
        string? UpdateScript = null)
    {
        public bool Success => Plan is not null;
    }

    public static async Task<DiffResult> RunAsync(
        IDatabaseProvider provider, ILedgerStore ledger,
        string schemaDir, string connectionString, bool allowDestructive,
        CancellationToken cancellationToken = default, string? migrationsDir = null, string? preDir = null)
    {
        // One load serves compare and analysis — the desired state is read and
        // classified exactly once per operation.
        var state = await provider.LoadDesiredStateAsync(schemaDir, cancellationToken);
        if (state.Errors.Count > 0)
        {
            return new DiffResult(null, state.Errors, FailureStage.DesiredState);
        }

        // Pre-stage scripts change what the comparison has to see — a rename turns drop+create into
        // nothing — so pending ones run first, in a simulation the provider never commits, and every
        // live read below goes through it. Read-only either way: nothing is recorded, and disposing the
        // simulation undoes the scripts. A provider that cannot simulate is refused rather than given
        // a plan computed as of before them, which would show exactly the drop+create they prevent.
        var prePlan = preDir is null
            ? null
            : await new MigrationRunner(provider, ledger, VersionedStage.Pre).PlanAsync(preDir, connectionString, cancellationToken);
        await using var simulation = prePlan is { Pending.Count: > 0 }
            ? await provider.BeginSimulationAsync(connectionString, cancellationToken)
            : null;
        if (prePlan is { Pending.Count: > 0 })
        {
            if (simulation is null)
            {
                // No way to plan as of after them here: the plan is the pre-stage alone, and the rest
                // is computed on the run after it (see PreStageOnly).
                return new DiffResult(PreStageOnly(provider, prePlan), Array.Empty<RawMessage>());
            }
            foreach (var script in prePlan.Pending)
            {
                try
                {
                    await provider.ExecuteScriptAsync(connectionString, script.Text, simulation, cancellationToken);
                }
                catch (Exception ex)
                {
                    // The engine aborts the transaction on the first failure; nothing after it can be read.
                    var detail = EngineErrors.Find(provider, ex)?.Describe() ?? ex.Message;
                    return new DiffResult(null, new[]
                    {
                        new RawMessage("Error", "pre_stage_failed",
                            $"Pre-stage script {script.FileName} failed against the database: {detail}"),
                    }, FailureStage.DesiredState);
                }
            }
        }

        var compared = await provider.CompareAsync(new CompareRequest(state, connectionString, simulation, WithholdColumnDrops: !allowDestructive), cancellationToken);
        // Classification skip warnings (deploy scripts, seed DML) surface once
        // per operation, riding the compare messages into the plan.
        var messages = state.Warnings.Concat(compared.Messages).ToList();
        if (messages.Any(m => m.Severity == "Error"))
        {
            return new DiffResult(null, messages, FailureStage.Compare);
        }

        // Strategy 2: pending idempotent re-definitions join the plan (read-only —
        // checksums against the ledger; a missing ledger table reads as no history).
        var programmables = await provider.AnalyzeProgrammablesAsync(state, cancellationToken);
        if (programmables.Messages.Any(m => m.Severity == "Error"))
        {
            return new DiffResult(null, programmables.Messages, FailureStage.DesiredState);
        }
        // Dialect knowledge only a connection can supply (e.g. PostgreSQL: can
        // CREATE OR REPLACE VIEW express this file's column list against what is
        // live?) — a no-op for a provider with no such gap.
        programmables = await provider.RefineProgrammablesAsync(programmables, state, connectionString, cancellationToken, simulation);
        if (programmables.Messages.Any(m => m.Severity == "Error"))
        {
            return new DiffResult(null, programmables.Messages, FailureStage.DesiredState);
        }
        var redefinePlan = RedefineRunner.WithInvalidations(
            await new RedefineRunner(provider, ledger).PlanAsync(programmables, connectionString, cancellationToken, simulation),
            programmables, compared.TablesWithColumnChanges, compared.ProgrammablesDroppedFirst);

        // Pending migrations run in the same apply, so the plan an apply is gated on names them
        // (read-only: the ledger is consulted, nothing runs). Invalid migration inputs — a duplicate
        // version, an applied migration edited since — throw here as they do for apply.
        var migrations = migrationsDir is null
            ? Array.Empty<PlanMigration>()
            : (await new MigrationRunner(provider, ledger).PlanAsync(migrationsDir, connectionString, cancellationToken)).ForPlan;

        return new DiffResult(
            PlanBuilder.Build(compared with { Messages = messages }, allowDestructive,
                redefinePlan.Pending.Select(p => p.ToPlanAction()).ToList(),
                provider.Capabilities.PlanAtomicity) with
            {
                Migrations = migrations,
                PreScripts = prePlan?.ForPlan ?? Array.Empty<PlanMigration>(),
            },
            Array.Empty<RawMessage>(),
            UpdateScript: compared.UpdateScript);
    }

    /// <summary>
    /// The plan when pre-stage scripts are pending and the provider cannot plan as of after them — its
    /// DDL is not transactional, or its comparison cannot read through a transaction (SQL Server: the
    /// engine compares on a connection of its own). The plan is the pre-stage alone: an apply of it runs
    /// the scripts, each committed with its ledger row, and stops; the next diff and apply see them done
    /// and plan the rest. Planning the rest now would compare against the schema before the scripts —
    /// exactly the drop+create they exist to prevent. Migrations wait for that next run too: they run
    /// after the declarative stage, which this run does not reach.
    /// </summary>
    internal static Plan PreStageOnly(IDatabaseProvider provider, MigrationPlan prePlan) =>
        new Plan(Plan.CurrentFormatVersion, Array.Empty<PlanAction>(), new[]
        {
            new PlanMessage("Warning", "SCHEMORPH014",
                $"{prePlan.Pending.Count} pre-stage script(s) pending, and this provider cannot plan as of after " +
                "them: this plan runs them only. Apply it, then diff and apply again — the rest is planned " +
                "against the schema they leave."),
        }, provider.Capabilities.PlanAtomicity)
        {
            PreScripts = prePlan.ForPlan,
        };
}
