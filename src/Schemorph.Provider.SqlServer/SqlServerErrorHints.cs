namespace Schemorph.Provider.SqlServer;

/// <summary>
/// Curated hints for the SQL Server error numbers a schema operation can actually surface — the
/// same failure classes the PostgreSQL provider curates by SQLSTATE (dependent objects, constraint
/// violations against existing rows, authentication, a missing database, contention, missing or
/// duplicate objects, permissions, truncation, timeouts), keyed by the server's own <c>Msg N</c>
/// (https://learn.microsoft.com/sql/relational-databases/errors-events/database-engine-events-and-errors).
/// Not a translation of the catalog. A number with no entry keeps no hint and is shown as
/// untranslated rather than given a guessed one: a hint for an unfamiliar number would send the
/// reader after the wrong thing, which is worse than the server's message alone.
/// </summary>
internal static class SqlServerErrorHints
{
    private const string DependentObject =
        "Another object — typically a view, a constraint or a schema-bound function — still depends on what this " +
        "statement drops or alters. Drop or change the dependent object first, then re-run.";

    private const string DuplicateKey =
        "A unique constraint or index was added against a column that already has duplicate values — " +
        "de-duplicate the data first.";

    private const string Authentication =
        "Login failed — check the user and password in the connection string, and that the login is enabled.";

    private const string Permission =
        "The connecting login lacks a permission this change requires (e.g. CREATE TABLE, ALTER on the schema, " +
        "or REFERENCES on a referenced table) — grant it, or use a more privileged login.";

    private const string AlreadyExists =
        "The object already exists — this can happen when the live database changed after the plan was " +
        "reviewed; re-run diff and compare against the current plan before retrying.";

    private const string Truncation =
        "A column was narrowed below the length of data it already holds — shorten or clean the data first, " +
        "or keep the wider type.";

    private static readonly IReadOnlyDictionary<int, string> ByNumber = new Dictionary<int, string>
    {
        [-2] = "The statement exceeded the client command timeout — rebuilding a large table (a column type change, " +
               "a new index) can take longer than the default timeout; raise the timeout in the connection string.",
        [207] = "References a column that does not exist — check the desired state for a typo, or a statement-ordering " +
                "issue against a column an earlier statement should have added.",
        [208] = "References a table or view that does not exist — check the desired state for a typo, or a " +
                "statement-ordering issue (a referenced object must already exist earlier in the script).",
        [229] = Permission,
        [262] = Permission,
        [515] = "A NOT NULL constraint was added against a column that already has NULLs — backfill the column " +
                "first, or add it nullable and tighten it once the data is clean.",
        [547] = "A foreign key or CHECK constraint was added or is being enforced against rows that violate it — " +
                "fix the data first (or add the constraint WITH NOCHECK and validate it later).",
        [1205] = "Chosen as the deadlock victim against concurrent activity on the database — safe to retry.",
        [1222] = "An object is locked by another session and the lock wait timed out — retry once that session releases it.",
        [1505] = DuplicateKey,
        [1785] = "A foreign key with ON DELETE/UPDATE CASCADE or SET NULL would give a table more than one cascade " +
                 "path (or a cycle); SQL Server refuses it — make one of the paths NO ACTION.",
        [1913] = AlreadyExists,
        [2601] = DuplicateKey,
        [2627] = DuplicateKey,
        [2628] = Truncation,
        [2714] = AlreadyExists,
        [3729] = DependentObject,
        [3906] = "The database is read-only (e.g. a readable secondary, or set READ_ONLY) — point the connection " +
                 "string at a writable primary.",
        [3960] = "Lost an update conflict with concurrent activity under snapshot isolation — safe to retry.",
        [4060] = "The database named in the connection string does not exist, or the login cannot open it.",
        [4922] = DependentObject,
        [5074] = DependentObject,
        [8115] = "A value already in the data does not fit the numeric type it is converted to — a column narrowed " +
                 "below the values it holds, or a re-defined view whose cast those values overflow. Widen the type or " +
                 "fix the data first.",
        [8152] = Truncation,
        [18456] = Authentication,
    };

    /// <summary>The known hint for server error <paramref name="number"/>, or <c>null</c> for an unrecognized one.</summary>
    public static string? TryGet(int number) => ByNumber.GetValueOrDefault(number);
}
