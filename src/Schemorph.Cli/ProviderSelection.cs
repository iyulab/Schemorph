using Schemorph.Core.Ledger;
using Schemorph.Core.Providers;
using Schemorph.Provider.SqlServer;

namespace Schemorph.Cli;

/// <summary>
/// The composition root's one point of provider construction. Today it selects
/// nothing — there is a single provider — and that is precisely the point: when a
/// second one arrives ([ADR-0003](../../docs/adr/0003-postgres-as-second-provider.md)),
/// the choice is made here once and every surface inherits it, instead of each CLI
/// verb, MCP tool, and MCP resource naming a concrete provider itself.
///
/// The provider and its ledger store are handed out as a pair because they must
/// agree on dialect — a provider paired with another provider's ledger store is a
/// silently broken target, and two independent factories would permit it.
///
/// Deliberately not a plugin registry, a DI registration, or a <c>--provider</c>
/// flag: ADR-0003 leaves the selection surface unspecified until a second
/// implementation exists to shape it. This is the seam, not the mechanism.
/// </summary>
internal readonly record struct ProviderSelection(IDatabaseProvider Provider, ILedgerStore Ledger)
{
    public static ProviderSelection Current => new(new SqlServerProvider(), new SqlServerLedgerStore());
}
