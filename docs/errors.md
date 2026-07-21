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

In text mode the same information renders as `error[<code>]: <message> (<hint>)`,
or `error[<code>]: <message>` when there is no hint.

- **`kind`** — stable coarse category. Branch on this.
- **`code`** — the specific failure. Stable, but new codes appear as features do.
- **`message`** / **`hint`** — human-oriented; no stability guarantee, never parse.

Optional fields are **absent, not null**, so an error that has none renders exactly
the four-field object above.

- **`hint`** is omitted when the tool has not established a cause. This is
  deliberate: a hint that names something never checked sends the reader after the
  wrong thing, which is worse than saying nothing.

### A failed `apply`: `stage` and `committed`

`apply` runs three strategies in order and does **not** roll back across them
([ADR-0004](adr/0004-failure-semantics-and-resume.md)), so what already committed
depends on where it stopped. A failed apply says so:

```json
{
  "error": {
    "kind": "execution",
    "code": "migration_execution_failed",
    "message": "Migration V7__backfill.sql failed: Invalid object name 'dbo.NoSuchTable'.",
    "hint": "Committed before the failure: 8 declarative change(s), 23 re-definition(s), 0 migration(s). Fix the failing object and re-run — apply is convergent; see docs/failure-semantics.md.",
    "stage": "migration",
    "committed": { "declarative": 8, "redefines": 23, "migrations": 0 }
  }
}
```

- **`stage`** — `desiredState` failures and `planMismatch` never reach the database,
  so they carry no stage; `redefine` and `migration` are the stages that can leave
  work behind. The declarative publish is one transaction: it commits fully or not
  at all.
- **`committed`** — counts, not names. The ledger is the per-object record
  ([failure-semantics.md](failure-semantics.md) explains how to read it); this
  exists so a caller can learn *whether anything changed* without querying.

Re-running is the resume path — apply converges. Never finish a failed apply by
hand: the ledger is what makes the run-once and redefine contracts hold.

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
| `temp_workspace_unavailable` | `usage` | Schemorph could not create the directory it keeps its own intermediate files in (`<TMP>/schemorph`). Point `TMP`/`TEMP` at a writable directory — these files are the tool's, not yours, and it never names them in an error |
| `invalid_desired_state` | `invalid_state` | Desired-state files fail to load or validate (e.g. `SCHEMORPH003/004/007`) — same code on every verb, `diff`/`status`/`apply` alike |
| `migration_failed` | `invalid_state` | Duplicate versions, or an applied migration was edited (tamper detection) — found before any migration runs |
| `redefine_failed` | `invalid_state` | Dependency cycle among programmable objects — found before any re-definition runs |
| `redefine_execution_failed` | `execution` | A re-definition script failed against the database. Carries `stage` and `committed`; the declarative publish had already committed |
| `migration_execution_failed` | `execution` | A migration script failed against the database. Carries `stage` and `committed` |
| `plan_mismatch` | `invalid_state` | `apply --expect-plan` (or MCP `expectedPlanHash`): the plan computed at apply time differs from the reviewed fingerprint — nothing was applied; re-run `diff`, review, retry with the new hash |
| `compare_failed` | `execution` | `diff` could not compare (connection, engine error) |
| `apply_failed` | `execution` | `apply` failed (publish errors, script failure, connection) |
| `inspect_failed` | `execution` | `inspect` failed |
| `review_script_unavailable` | `execution` | `diff --format sql`: the plan has declarative changes but the engine could not generate their update script (`SCHEMORPH002`). No document is emitted — a partial approval artifact is worse than none |
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
| `SCHEMORPH008` | Warning | The comparison could not read the target completely. The engine's own reason is echoed in the message. It accompanies an engine error, so the verb fails — see below |

#### An incomplete comparison is a failure, not a partial answer

`SCHEMORPH008` never rides a successful plan. The engine reports the incompleteness
as an *error*, and both verbs stop there: `diff` fails with `compare_failed` (exit
`1`), `apply` refuses before touching anything. There is no partial plan and no
`hasChanges: false` for automation to mistake for an in-sync database — a plan
exists only when the whole target was read.

The warning is what names the reason inside that failure, which is why it is worth
reading rather than just the envelope.

#### `SCHEMORPH008` fires on an incomplete comparison, not on a missing permission

The warning reports an **effect**: the comparison came back without having read the
whole target. It deliberately does not key on any particular grant, because
permissions turn out not to predict completeness in either direction. Measured
against a live SQL Server, same database and same desired state, differing only in
what the login may read:

| Login | server `VIEW ANY DEFINITION` | database `VIEW DEFINITION` | on the changed object | comparison |
|---|---|---|---|---|
| `db_owner`, no server-scope grant | ✗ | ✓ | ✓ | **complete** |
| `db_datareader` + `DENY VIEW DEFINITION` | ✗ | ✗ | ✗ | **empty** |
| database-scope `GRANT` + object-level `DENY` | ✗ | ✓ | ✗ | **empty** |

The first row is the least-privilege shape this project's own guidance recommends,
and it reads every database object — so firing on "lacks `VIEW ANY DEFINITION`"
warns exactly the setups that are fine. The third row is why checking the
database-scoped permission instead would be worse than the problem: it looks
granted while the comparison still returns nothing, which turns a false alarm into
a silent miss.

DacFx's own warning is narrower than it first reads — it restricts the comparison
"to database scoped elements *if the source is a database*", and this tool's source
is always a dacpac built from the desired-state files. What it does report, in the
incomplete cases only, is an error: *the reverse engineering operation cannot
continue because you do not have View Definition permission on the '…' database* /
*…because you have been denied View Definition permission on at least one object in
the '…' database*. `SCHEMORPH008` fires on that and echoes it verbatim, because the
two reasons need different fixes and guessing between them helps nobody.

**Programmable-object re-definition is a separate path and is not gated by this at
all.** Views, procedures, functions, and triggers go through the idempotent redefine
strategy (ADR-0002), which reads live bodies with its own `sys.sql_modules` query.
That column is visible to any principal with `VIEW DEFINITION` — or `CONTROL` /
`ALTER` / `TAKE OWNERSHIP` — on the object or database ([Metadata visibility configuration](https://learn.microsoft.com/sql/relational-databases/security/metadata-visibility-configuration)),
so a `db_owner` login reads every body through its `CONTROL`. A programmable object
that is *absent* from the plan was reconciled — its live body matched the file —
never "skipped for lack of permission": an unreadable body makes the object
**appear** as a redefine, not vanish.

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
