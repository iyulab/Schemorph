namespace Schemorph.Provider.Postgres;

/// <summary>
/// Curated hints for the PostgreSQL SQLSTATE codes an apply's declarative publish can actually
/// surface (Appendix A, https://www.postgresql.org/docs/current/errcodes-appendix.html) — not a
/// translation of the ~300-code catalog. <see cref="PostgresProvider.ApplyAsync"/> previously
/// passed <c>ex.SqlState</c>/<c>ex.MessageText</c> straight through with no interpretation; this
/// gives the codes an operator actually hits during schema apply a short, actionable addition,
/// and leaves everything else untouched — a guessed hint for an unfamiliar code would send the
/// reader after the wrong thing, which is worse than the raw message alone.
/// </summary>
internal static class PgSqlStateHints
{
    private static readonly IReadOnlyDictionary<string, string> ByCode = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["23502"] = "A NOT NULL constraint was added against a column that already has NULLs — " +
                    "backfill the column first, or add it nullable and tighten it once the data is clean.",
        ["23503"] = "A foreign key was added or is being enforced against rows that violate it — " +
                    "fix the referencing data first (or add the constraint NOT VALID and validate it later).",
        ["23505"] = "A unique constraint or index was added against a column that already has " +
                    "duplicate values — de-duplicate the data first.",
        ["23514"] = "A CHECK constraint was added against rows that violate it — bring the data " +
                    "into compliance first (or add the constraint NOT VALID and validate it later).",
        ["23P01"] = "An exclusion constraint rejected an existing or incoming row — resolve the " +
                    "conflicting rows first.",
        ["25006"] = "The connection is to a read-only session (e.g. a hot standby replica) — point " +
                    "the URL at the primary.",
        ["28000"] = "Authentication failed — check the role and its permissions in the connection string.",
        ["28P01"] = "Authentication failed — check the password in the connection string.",
        ["3D000"] = "The database named in the connection string does not exist.",
        ["40001"] = "Lost a serialization conflict with concurrent activity on the database — safe to retry.",
        ["40P01"] = "A deadlock was detected against concurrent activity on the database — safe to retry.",
        ["42501"] = "The connecting role lacks a permission this change requires (e.g. CREATE on the " +
                    "schema, or ownership of the object) — grant it, or use a more privileged role.",
        ["42703"] = "References a column that does not exist — check the desired state for a typo, " +
                    "or a statement-ordering issue against a column an earlier statement should have added.",
        ["42P01"] = "References a table that does not exist — check the desired state for a typo, " +
                    "or a statement-ordering issue (a referenced table must already exist earlier in the script).",
        ["42710"] = "The object already exists — this can happen when the live database changed after " +
                    "the plan was reviewed; re-run diff and compare against the current plan before retrying.",
        ["42P07"] = "The table already exists — this can happen when the live database changed after " +
                    "the plan was reviewed; re-run diff and compare against the current plan before retrying.",
        ["53300"] = "The server has no free connection slots — retry once load drops, or raise max_connections.",
        ["55006"] = "The object is locked by another session — retry once that session releases it.",
        ["57014"] = "The statement exceeded statement_timeout — a full-table rewrite (adding a column " +
                    "with a volatile default, or building an index) can take longer than the default " +
                    "timeout on a large table.",
    };

    /// <summary>The known hint for <paramref name="sqlState"/>, or <c>null</c> for an unrecognized code.</summary>
    public static string? TryGet(string sqlState) => ByCode.GetValueOrDefault(sqlState);
}
