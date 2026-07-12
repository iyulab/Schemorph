using Schemorph.Core.Ledger;
using Schemorph.Core.Planning;
using Schemorph.Core.Providers;
using Schemorph.Core.Redefine;

namespace Schemorph.Core.Operations;

/// <summary>
/// The diff orchestration, owned by the core so every surface (CLI verb, MCP
/// tool) renders the same operation instead of re-wiring it (architecture.md:
/// "two renderings of the same core API"). Read-only: compares, analyzes
/// programmables, judges redefines/reconciles — never writes to the database.
/// </summary>
public static class DiffOperation
{
    /// <summary>Which stage produced the errors — callers map this to their error vocabulary.</summary>
    public enum FailureStage { None, Compare, DesiredState }

    public sealed record DiffResult(Plan? Plan, IReadOnlyList<RawMessage> Errors, FailureStage Stage = FailureStage.None)
    {
        public bool Success => Plan is not null;
    }

    public static async Task<DiffResult> RunAsync(
        IDatabaseProvider provider, ILedgerStore ledger,
        string schemaDir, string connectionString, bool allowDestructive,
        CancellationToken cancellationToken = default)
    {
        // One load serves compare and analysis — the desired state is read and
        // classified exactly once per operation.
        var state = await provider.LoadDesiredStateAsync(schemaDir, cancellationToken);
        if (state.Errors.Count > 0)
        {
            return new DiffResult(null, state.Errors, FailureStage.DesiredState);
        }

        var compared = await provider.CompareAsync(new CompareRequest(state, connectionString), cancellationToken);
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
        var redefinePlan = await new RedefineRunner(provider, ledger)
            .PlanAsync(programmables, connectionString, cancellationToken);

        return new DiffResult(
            PlanBuilder.Build(compared with { Messages = messages }, allowDestructive,
                redefinePlan.Pending.Select(p => p.ToPlanAction()).ToList()),
            Array.Empty<RawMessage>());
    }
}
