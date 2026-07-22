namespace Schemorph.Core.Providers;

/// <summary>
/// What an apply guarantees when it fails partway (ADR-0004 addendum): the
/// same command must not mean different things depending on the database, so
/// the guarantee is declared rather than assumed from the tool.
/// </summary>
public enum ApplyAtomicity
{
    /// <summary>
    /// The apply is one unit: it lands whole or not at all. A provider may
    /// claim this only where the tool holds the transaction boundary — owning
    /// the connection, not merely observing that something rolled back.
    /// </summary>
    Transactional,

    /// <summary>
    /// Stages commit independently; a failure leaves earlier stages applied.
    /// ADR-0004's decisions describe this mode exactly.
    /// </summary>
    Partial,
}

/// <summary>
/// A provider's declared surface — the canonical layer of the three-layer
/// exposure policy (dev plan §2): the manifest states what the provider claims
/// to handle, the plan/status envelope states what this run guaranteed
/// (<see cref="Plan"/>'s <c>atomicity</c>), and the typed refusal
/// (<see cref="UnsupportedByProviderException"/>) is the backstop for
/// everything outside the claim. The declaration and the refusals pin each
/// other in tests, so a capability line is never decorative.
/// </summary>
/// <param name="Declared">
/// Capability lines, the parity yardstick between providers (a slice of work
/// ends by adding one line). Vocabulary: <c>inspect</c>, <c>tables</c>,
/// <c>columns</c>, <c>constraints</c>, <c>schemas</c>, <c>indexes</c>,
/// <c>views</c>, <c>functions</c>, <c>triggers</c>, <c>procedures</c>,
/// <c>migrations</c>.
/// </param>
/// <param name="Atomicity">
/// The apply guarantee, or null while the provider declares no apply-side
/// capability at all — a provider that cannot apply must not claim what an
/// apply would guarantee.
/// </param>
public sealed record ProviderCapabilities(
    IReadOnlyList<string> Declared,
    ApplyAtomicity? Atomicity)
{
    /// <summary>
    /// The atomicity a computed plan carries. A provider that reaches plan
    /// building without declaring an apply guarantee violates its own
    /// contract — that is a defect in the provider, not a user error.
    /// </summary>
    public ApplyAtomicity PlanAtomicity => Atomicity ?? throw new InvalidOperationException(
        "This provider computes plans but declares no apply atomicity (ADR-0004 addendum).");
}
