using Schemorph.Core.Errors;
using Schemorph.Core.Ledger;
using Schemorph.Core.Providers;
using Schemorph.Provider.Postgres;
using Schemorph.Provider.SqlServer;

namespace Schemorph.Cli;

/// <summary>
/// The composition root's one point of provider construction — the choice is
/// made here once and every surface (CLI verb, MCP tool, MCP resource)
/// inherits it, instead of naming a concrete provider itself.
///
/// The selection surface ADR-0003 deferred until a second implementation
/// existed is now shaped by that implementation: the <c>SCHEMORPH_PROVIDER</c>
/// environment variable, defaulting to SQL Server so no existing consumer
/// changes anything. An environment variable rather than connection-string
/// sniffing (two ADO.NET dialects overlap enough to misroute) and rather than
/// a per-verb flag (the provider is a property of the environment a command
/// runs in, like SCHEMORPH_URL — and MCP servers configure exactly that way).
///
/// The provider and its ledger store are handed out as a pair because they must
/// agree on dialect — a provider paired with another provider's ledger store is a
/// silently broken target, and two independent factories would permit it.
/// </summary>
internal readonly record struct ProviderSelection(IDatabaseProvider Provider, ILedgerStore Ledger)
{
    public const string EnvironmentVariable = "SCHEMORPH_PROVIDER";

    public static ProviderSelection Current =>
        Environment.GetEnvironmentVariable(EnvironmentVariable)?.Trim().ToLowerInvariant() switch
        {
            null or "" or SqlServerProvider.ProviderName => new(new SqlServerProvider(), new SqlServerLedgerStore()),
            PostgresProvider.ProviderName => new(new PostgresProvider(), new PostgresLedgerStore()),
            var other => throw new InvalidOperationException(
                $"Unknown {EnvironmentVariable} value '{other}'. " +
                $"Valid values: {SqlServerProvider.ProviderName} (default), {PostgresProvider.ProviderName}."),
        };

    /// <summary>
    /// The one wording for a connection string the selected SQL Server provider cannot read. SQL Server is the
    /// default, so the first failure of a PostgreSQL user who has not set <see cref="EnvironmentVariable"/> is the
    /// driver's own "Keyword not supported: 'host'" — true, and silent about the way out. The cause is established
    /// (the provider in effect is SQL Server and the driver rejected a keyword), so the hint states it as fact and
    /// offers the PostgreSQL remedy conditionally. The provider is still never chosen from the connection string
    /// (see the class remarks): this names the variable, it does not set it.
    /// </summary>
    public static string? HintForUnsupportedKeyword(string message) =>
        message.Contains("Keyword not supported", StringComparison.Ordinal) && CurrentProviderOrNull is SqlServerProvider
            ? $"The SQL Server provider is in effect (the default) and does not recognize a keyword in this connection string. " +
              $"If it is a PostgreSQL connection string, set {EnvironmentVariable}={PostgresProvider.ProviderName}."
            : null;

    /// <summary>The envelope with that hint filled in when it has none of its own.</summary>
    public static SchemorphError WithConnectionHint(SchemorphError error) =>
        error.Hint is null && HintForUnsupportedKeyword(error.Message) is { } hint ? error with { Hint = hint } : error;

    /// <summary>
    /// The selected provider, or null when the selection itself is invalid, for the
    /// error paths: they must still produce an envelope when the failure being
    /// reported is exactly that invalid selection.
    /// </summary>
    public static IDatabaseProvider? CurrentProviderOrNull
    {
        get
        {
            try { return Current.Provider; }
            catch (InvalidOperationException) { return null; }
        }
    }
}
