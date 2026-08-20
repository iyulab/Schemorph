namespace Schemorph.Core.Providers;

/// <summary>
/// An open apply-unit transaction boundary a provider owns (ADR-0004
/// addendum): opaque to the core, which only opens, commits, rolls back and
/// disposes it — never inspects what is inside. A provider that declares
/// <see cref="ApplyAtomicity.Partial"/> never produces one
/// (<see cref="IDatabaseProvider.BeginApplySessionAsync"/> returns null); a
/// provider that declares <see cref="ApplyAtomicity.Transactional"/> must,
/// and every execution call made during that apply is handed this session so
/// its work lands in the one boundary the declaration promises.
/// </summary>
public interface IApplySession : IAsyncDisposable
{
    /// <summary>Commits everything executed through this session. Called once, when the whole apply succeeded.</summary>
    Task CommitAsync(CancellationToken cancellationToken = default);

    /// <summary>Discards everything executed through this session. Called once, on the first execution failure.</summary>
    Task RollbackAsync(CancellationToken cancellationToken = default);
}
