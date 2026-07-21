using System.Text.RegularExpressions;

namespace Schemorph.Core;

/// <summary>
/// Masks credential material in outgoing text. Agent-first positioning means
/// every output gets re-propagated mechanically (logs, PR comments, ledger rows),
/// so redaction is applied at the sinks: the CLI output boundary and persisted
/// ledger failure detail.
/// </summary>
public static partial class Redaction
{
    public static string Redact(string text) => PasswordPattern().Replace(text, "$1=***");

    /// <summary>Redaction over an absent value — absent stays absent.</summary>
    public static string? RedactOrNull(string? text) => text is null ? null : Redact(text);

    [GeneratedRegex(@"\b(password|pwd)\s*=\s*[^;""'\s]+", RegexOptions.IgnoreCase)]
    private static partial Regex PasswordPattern();
}
