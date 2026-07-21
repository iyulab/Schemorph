namespace Schemorph.Provider.Postgres;

/// <summary>
/// The catalog as Schemorph reads it — plain data, no engine types, no
/// connection. Everything downstream of the reader works on this, which is what
/// keeps rendering testable without a database.
/// </summary>
/// <param name="DataType">
/// The engine's own rendering (<c>format_type</c>), not a mapping of ours. The
/// engine is the normalizer (ADR-0007); re-spelling types here would reintroduce
/// exactly the text-comparison drift that decision exists to avoid.
/// </param>
public sealed record PgColumn(string Name, string DataType, bool NotNull, string? Default);

/// <param name="Definition">
/// From <c>pg_get_constraintdef</c> — the engine's canonical form, e.g.
/// <c>PRIMARY KEY ("Id")</c>. Emitted verbatim for the same reason.
/// </param>
public sealed record PgConstraint(string Name, string Definition);

/// <param name="CreateStatement">
/// From <c>pg_indexes.indexdef</c> — already a complete CREATE INDEX statement.
/// </param>
public sealed record PgIndex(string Name, string CreateStatement);

public sealed record PgTable(
    string Schema,
    string Name,
    IReadOnlyList<PgColumn> Columns,
    IReadOnlyList<PgConstraint> Constraints,
    IReadOnlyList<PgIndex> Indexes);
