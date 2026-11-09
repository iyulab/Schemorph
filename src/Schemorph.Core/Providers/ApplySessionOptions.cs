namespace Schemorph.Core.Providers;

/// <summary>
/// What a caller asks of the apply-unit transaction a provider opens
/// (<see cref="IDatabaseProvider.BeginApplySessionAsync"/>). Every member is opt-in: a
/// default-constructed value asks for nothing, and the session behaves exactly as it did
/// before the member existed.
/// </summary>
/// <param name="LockTimeout">
/// How long any statement of the apply may wait for a lock held by another session before it
/// fails. Null — the default — leaves the server's own setting in force (for PostgreSQL: wait
/// indefinitely). Applies for the life of the apply's transaction only. A provider that opens
/// no session cannot honor it; the core refuses the request before touching the database.
/// </param>
public sealed record ApplySessionOptions(TimeSpan? LockTimeout = null)
{
    /// <summary>The longest timeout PostgreSQL's <c>lock_timeout</c> can hold (a signed 32-bit millisecond count).</summary>
    public static readonly TimeSpan MaxLockTimeout = TimeSpan.FromMilliseconds(int.MaxValue);

    /// <summary>
    /// Parses a lock-timeout argument: a whole number with a unit — <c>ms</c>, <c>s</c> or <c>m</c>
    /// (<c>500ms</c>, <c>30s</c>, <c>2m</c>). The unit is required: a bare number would be read as
    /// seconds by one person and milliseconds by another, and the server's own default unit is
    /// milliseconds. Zero is refused — in the server it means "no timeout", which is what omitting
    /// the option already says.
    /// </summary>
    public static bool TryParseLockTimeout(string? text, out TimeSpan value, out string error)
    {
        value = default;
        error = "";
        var s = text?.Trim() ?? "";
        var (digits, unit) = SplitUnit(s);
        if (digits.Length == 0 || !digits.All(char.IsAsciiDigit) || !long.TryParse(digits, out var n))
        {
            error = $"'{text}' is not a lock timeout. Use a whole number with a unit: 500ms, 30s or 2m.";
            return false;
        }
        if (n == 0)
        {
            error = "A lock timeout of 0 means no timeout; leave the option out instead.";
            return false;
        }
        TimeSpan span;
        try
        {
            span = unit switch
            {
                "ms" => TimeSpan.FromMilliseconds(n),
                "s" => TimeSpan.FromSeconds(n),
                "m" => TimeSpan.FromMinutes(n),
                _ => throw new FormatException(),
            };
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException)
        {
            error = $"'{text}' is not a lock timeout. Use a whole number with a unit: 500ms, 30s or 2m.";
            return false;
        }
        if (span > MaxLockTimeout)
        {
            error = $"A lock timeout cannot exceed {(long)MaxLockTimeout.TotalMilliseconds} ms (about 24 days).";
            return false;
        }
        value = span;
        return true;
    }

    private static (string Digits, string Unit) SplitUnit(string s)
    {
        if (s.EndsWith("ms", StringComparison.Ordinal)) return (s[..^2], "ms");
        if (s.EndsWith('s')) return (s[..^1], "s");
        if (s.EndsWith('m')) return (s[..^1], "m");
        return (s, "");
    }
}
