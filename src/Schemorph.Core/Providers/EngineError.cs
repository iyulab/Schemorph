namespace Schemorph.Core.Providers;

/// <summary>
/// An error the database engine raised, described by the provider that talks to it
/// (<see cref="IDatabaseProvider.DescribeEngineError"/>). <see cref="Code"/> and
/// <see cref="Message"/> are the engine's own — a PostgreSQL SQLSTATE, a SQL Server
/// error number, a DacFx message code — and <see cref="Hint"/> is the provider's
/// translation into what to do next. A code the provider has no translation for
/// keeps a null hint and is marked as untranslated wherever it is shown, rather
/// than dressed in a guess: a hint that names the wrong cause sends the reader
/// after it, which is worse than the engine's text alone.
/// </summary>
public sealed record EngineError(string Code, string Message, string? Hint)
{
    /// <summary>The words that mark an engine error Schemorph has no translation for.</summary>
    public const string UntranslatedMarker = "untranslated engine error";

    /// <summary>Whether the provider translated this code into a next step.</summary>
    public bool Translated => Hint is not null;

    /// <summary>
    /// The engine's message followed by the translation, or by the untranslated
    /// marker — the one wording every stage uses, so the same engine failure reads
    /// the same wherever it surfaced. The code is not repeated here: every caller
    /// already shows it in its own slot (a message code, or <see cref="Describe"/>).
    /// </summary>
    public string Text => $"{Message} ({Hint ?? $"{UntranslatedMarker} — the database's own message, passed through unchanged"})";

    /// <summary><see cref="Code"/> and <see cref="Text"/> together, for a slot that has no separate code.</summary>
    public string Describe() => $"{Code}: {Text}";

    /// <summary>The machine-readable part of this error, as the error envelope carries it.</summary>
    public EngineErrorInfo Info => new(Code, Translated);
}

/// <summary>
/// The engine error behind a failure, as the error envelope exposes it: the engine's
/// own code, and whether Schemorph translated it — so automation can tell "the tool
/// knows what this means" from "this is the database talking" without parsing text.
/// </summary>
public sealed record EngineErrorInfo(string Code, bool Translated);

/// <summary>Finding the engine error behind a failure.</summary>
public static class EngineErrors
{
    /// <summary>
    /// The first engine error <paramref name="provider"/> recognizes in
    /// <paramref name="exception"/> or anything it wraps — a stage's own exception
    /// type (a failed re-definition, a failed migration) carries the engine error
    /// inside it, and an <see cref="AggregateException"/> may too.
    /// </summary>
    public static EngineError? Find(IDatabaseProvider provider, Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (provider.DescribeEngineError(current) is { } found) return found;
            if (current is AggregateException { InnerExceptions.Count: > 1 } aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions)
                {
                    if (Find(provider, inner) is { } nested) return nested;
                }
                return null;
            }
        }
        return null;
    }
}
