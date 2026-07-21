using Schemorph.Core.Errors;

namespace Schemorph.Core.Providers;

/// <summary>
/// A provider was asked for something it does not implement. This is a declared
/// boundary, not a failure: it carries the machine contract (<c>not_implemented</c>
/// → kind <c>unsupported</c>) so a caller can tell "this provider does not do that
/// yet" from "that went wrong", which a generic exception cannot express.
///
/// The shape a provider grows into: it declares what it can do, and refuses the
/// rest rather than producing a result it cannot stand behind.
/// </summary>
public sealed class UnsupportedByProviderException : Exception
{
    public UnsupportedByProviderException(string providerName, string capability, string? supported = null)
        : base($"The '{providerName}' provider does not implement {capability} yet.")
    {
        ProviderName = providerName;
        Capability = capability;
        Supported = supported;
    }

    public string ProviderName { get; }

    /// <summary>The capability that was asked for, in the core's vocabulary.</summary>
    public string Capability { get; }

    /// <summary>
    /// What the provider does declare, for the hint. Null when it declares nothing —
    /// silence beats naming a direction the tool has not verified.
    /// </summary>
    public string? Supported { get; }

    public SchemorphError ToError() => SchemorphError.Create(
        "not_implemented",
        Message,
        hint: Supported is null ? null : $"'{ProviderName}' currently supports: {Supported}.");
}
