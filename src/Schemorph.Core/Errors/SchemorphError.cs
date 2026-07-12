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
    public static SchemorphError Create(string code, string message, string? hint = null)
        => new(KindOf(code), code, message, hint);

    /// <summary>Single source of truth for the code → kind mapping.</summary>
    public static string KindOf(string code) => code switch
    {
        "invalid_arguments" or "schema_dir_not_found" or "migrations_dir_not_found"
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
        "compare_failed" or "apply_failed" or "inspect_failed"
            => "execution",
        _ => "internal",
    };
}
