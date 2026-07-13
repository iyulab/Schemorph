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
| `invalid_desired_state` | `invalid_state` | Desired-state files fail to load or validate (e.g. `SCHEMORPH003/004/007`) — same code on every verb, `diff`/`status`/`apply` alike |
| `migration_failed` | `invalid_state` | Duplicate versions, or an applied migration was edited (tamper detection) |
| `redefine_failed` | `invalid_state` | Dependency cycle among programmable objects |
| `plan_mismatch` | `invalid_state` | `apply --expect-plan` (or MCP `expectedPlanHash`): the plan computed at apply time differs from the reviewed fingerprint — nothing was applied; re-run `diff`, review, retry with the new hash |
| `compare_failed` | `execution` | `diff` could not compare (connection, engine error) |
| `apply_failed` | `execution` | `apply` failed (publish errors, script failure, connection) |
| `inspect_failed` | `execution` | `inspect` failed |
| `not_implemented` | `unsupported` | The verb exists but is not implemented yet |

## Provider messages

Distinct from the error envelope: providers attach `SCHEMORPH###`-coded messages to
plans and results (rendered under the plan in text mode, in `messages` in JSON).
Warnings never change the exit code.

| Code | Severity | Meaning |
|---|---|---|
| `SCHEMORPH001` | Warning | Destructive change excluded from the plan (pass `--allow-destructive` to include) |
| `SCHEMORPH002` | Warning | Update-script generation failed (plan is still valid) |
| `SCHEMORPH003` | Error | A programmable object has no source file in the desired state |
| `SCHEMORPH004` | Error | One file defines more than one programmable object (ADR-0002: one per file) |
| `SCHEMORPH005` | Warning | File skipped: SQLCMD syntax marks it as a deploy script, not desired state |
| `SCHEMORPH006` | Warning | File skipped: contains imperative statements (EXEC / DML) — not declarative DDL |
| `SCHEMORPH007` | Error | A `.sql` file failed to parse (file, line, and column are named) |
| `SCHEMORPH008` | Warning | The comparison was restricted (the connection lacks VIEW ANY DEFINITION) — some object definitions could not be read, so the plan may be incomplete; a partial result must not be read as "in sync" |

#### `SCHEMORPH008` scope: it is about the declarative diff only

`VIEW ANY DEFINITION` is a **server-scope** permission that DacFx's declarative
comparison (tables, columns, indexes, constraints) uses to read a complete target
model. Lacking it restricts *that* comparison, which is what `SCHEMORPH008` reports.

It does **not** gate programmable-object **re-definition**. Views, procedures,
functions, and triggers are routed to the idempotent redefine strategy (ADR-0002),
which reads live bodies through its own `sys.sql_modules` query. That column is
visible to any principal with `VIEW DEFINITION` — or `CONTROL` / `ALTER` /
`TAKE OWNERSHIP` — on the object or database ([Metadata visibility configuration](https://learn.microsoft.com/sql/relational-databases/security/metadata-visibility-configuration)),
so a **`db_owner`** login (which has `CONTROL` on its database) reads every body
without the server-scope grant. Consequently, a minimal-privilege `db_owner` still
re-defines existing views correctly; only the structural diff's *completeness*
degrades, and it degrades loudly (`SCHEMORPH008`) rather than silently. A
programmable object that is *absent* from the plan was reconciled — its live body
matched the file — never "skipped for lack of permission": an unreadable body makes
the object **appear** as a redefine, not vanish.

### Safety lint (`SCHEMORPH1xx`)

Lint findings over the plan, in their own code band. Always warnings — they inform
review and are machine-checkable (e.g. a CI policy failing on specific codes), but
never change the exit code; execution gating stays with the destructive gate.
Rules are deliberately conservative: they fire only on what is proven, so a
missing warning is possible but a wrong one is not.

| Code | Severity | Meaning |
|---|---|---|
| `SCHEMORPH101` | Warning | A change adds a NOT NULL column without a default — fails on a table that already holds rows |
| `SCHEMORPH102` | Warning | A change rebuilds the table (new table, rows copied, old dropped, renamed) — cost grows with the data |
| `SCHEMORPH103` | Warning | A destructive change is included in the plan (`--allow-destructive`) — applying it loses the data it holds |
| `SCHEMORPH104` | Warning | A pending migration TRUNCATEs a table — removes every row, not selectively recoverable |
| `SCHEMORPH105` | Warning | A pending migration UPDATEs or DELETEs without a WHERE clause — touches every row |
| `SCHEMORPH106` | Warning | A pending migration changes permissions (GRANT/REVOKE/DENY) |

Plan-side findings (`101`–`103`) ride the plan's `messages`; migration-side findings
(`104`–`106`) ride the `migrations.warnings` list on `status` and `apply` output
(text mode renders both under their section).
