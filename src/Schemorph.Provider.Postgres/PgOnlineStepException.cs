using Npgsql;

namespace Schemorph.Provider.Postgres;

/// <summary>
/// A statement of the online stage (ADR-0009) that the server rejected. The server's error is carried
/// unchanged; the wrapper only says where it happened, so <see cref="PostgresProvider.DescribeEngineError"/>
/// can choose the hint that is true for a statement run after the apply's transaction committed.
/// </summary>
internal sealed class PgOnlineStepException(PostgresException engine) : Exception(engine.Message, engine)
{
    /// <summary>The error the server raised.</summary>
    public PostgresException Engine { get; } = engine;
}
