namespace Schemorph.Core.Errors;

/// <summary>
/// Semantic exit codes (Terraform convention). Part of the machine contract:
/// agents branch on these, so values are frozen — additions only.
/// </summary>
public enum ExitCode
{
    Success = 0,
    Error = 1,
    ChangesPending = 2,
}

/// <summary>
/// The machine-readable error contract: <c>{kind, code, message, hint}</c>.
/// <c>Kind</c> is the stable coarse category consumers branch on; <c>Code</c>
/// identifies the specific failure; <c>Message</c>/<c>Hint</c> are for humans
/// (and agents) and carry no stability guarantee. Documented in docs/errors.md —
/// keep the two in sync.
/// </summary>
public sealed record SchemorphError(string Kind, string Code, string Message, string? Hint)
{
    /// <summary>
    /// For a failed <c>apply</c>: the stage it stopped in. Absent everywhere else.
    /// Apply runs three strategies in order with no rollback between them
    /// (ADR-0004), so "where it stopped" is the difference between a database
    /// that changed and one that did not — see docs/failure-semantics.md.
    /// </summary>
    public string? Stage { get; init; }

    /// <summary>
    /// For a failed <c>apply</c>: how much of each stage had committed before the
    /// failure. Counts, not names — the ledger is the per-object record, and this
    /// exists so a caller need not read it to learn whether anything changed.
    /// </summary>
    public CommittedWork? Committed { get; init; }

    public static SchemorphError Create(string code, string message, string? hint = null)
        => new(KindOf(code), code, message, hint);

    /// <summary>Single source of truth for the code → kind mapping.</summary>
    public static string KindOf(string code) => code switch
    {
        // temp_workspace_unavailable: the fix is environmental (TMP/TEMP), like the
        // rest of this band — retrying unchanged fails identically.
        "invalid_arguments" or "schema_dir_not_found" or "migrations_dir_not_found"
            or "temp_workspace_unavailable"
            => "usage",
        "not_implemented"
            => "unsupported",
        // The desired state (or its relationship to recorded history) is wrong;
        // the fix is editing files, not retrying.
        // plan_mismatch: reality moved since the diff was reviewed — re-run diff,
        // review again, pass the new hash.
        "invalid_desired_state" or "migration_failed" or "redefine_failed" or "plan_mismatch"
            => "invalid_state",
        // The operation itself failed against the database; retrying may help.
        // The two *_execution_failed codes are the redefine/migration stages
        // failing against the database — a different thing from the same-named
        // invalid_state codes above, which are desired-state problems found
        // before those stages run anything.
        // review_script_unavailable: the comparison succeeded but the engine could
        // not generate the script the review document is made of, so no document
        // is produced rather than a partial one.
        "compare_failed" or "apply_failed" or "inspect_failed"
            or "redefine_execution_failed" or "migration_execution_failed"
            or "review_script_unavailable"
            => "execution",
        _ => "internal",
    };
}

/// <summary>
/// What a failed apply had already committed, by strategy (ADR-0002 order).
/// <paramref name="Declarative"/> is all-or-nothing — the publish is one
/// transaction — while the other two are per-script progress counts.
/// </summary>
public sealed record CommittedWork(int Declarative, int Redefines, int Migrations);
