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
}
