# Errors and exit codes

Schemorph's error surface is a machine contract: agents and scripts branch on it.
The shapes below are stable — changes are additive only. The single source of truth
in code is `SchemorphError` (`src/Schemorph.Core/Errors/SchemorphError.cs`); keep
this document and that file in sync.

## Exit codes

Terraform's `-detailed-exitcode` convention:

| Exit | Meaning |
|---|---|
| `0` | Success; for `diff`, additionally: no changes pending |
| `1` | Error (an `error` envelope was written to stderr) |
| `2` | `diff` only: success, and changes are pending |

## The error envelope

With `--format json`, every failure writes exactly one JSON object to **stderr**:

```json
{
  "error": {
    "kind": "invalid_state",
    "code": "migration_failed",
    "message": "Migration V3__x.sql was modified after being applied (...)",
    "hint": "Applied migrations are immutable; add a new V####__*.sql instead of editing old ones."
  }
}
```

In text mode the same information renders as `error[<code>]: <message> (<hint>)`.

- **`kind`** — stable coarse category. Branch on this.
- **`code`** — the specific failure. Stable, but new codes appear as features do.
- **`message`** / **`hint`** — human-oriented; no stability guarantee, never parse.

## Kinds

| Kind | Meaning | Sensible reaction |
|---|---|---|
| `usage` | The invocation is wrong (missing/invalid arguments or paths) | Fix the command line |
| `invalid_state` | The desired-state files — or their relationship to recorded history — are wrong | Edit the files; retrying unchanged will fail identically |
| `execution` | The operation failed against the database (connectivity, engine errors) | Inspect the message; retrying can help (apply is safe to re-run, see ADR-0004) |
| `unsupported` | The verb/feature is not implemented | Consult the roadmap |
| `internal` | A code not yet classified — a Schemorph bug if ever observed | Report it |

## Codes

| Code | Kind | Emitted when |
|---|---|---|
| `invalid_arguments` | `usage` | Required options missing |
| `schema_dir_not_found` | `usage` | `--schema` directory does not exist |
| `migrations_dir_not_found` | `usage` | `--migrations` directory does not exist |
| `invalid_desired_state` | `invalid_state` | Desired-state files fail model validation (e.g. `SCHEMORPH003/004`) |
| `migration_failed` | `invalid_state` | Duplicate versions, or an applied migration was edited (tamper detection) |
| `redefine_failed` | `invalid_state` | Dependency cycle among programmable objects |
| `compare_failed` | `execution` | `diff` could not compare (connection, engine error) |
| `apply_failed` | `execution` | `apply` failed (publish errors, script failure, connection) |
| `inspect_failed` | `execution` | `inspect` failed |
| `not_implemented` | `unsupported` | The verb exists but is not implemented yet |
