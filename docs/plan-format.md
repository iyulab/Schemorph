# The plan format

The machine-readable plan is Schemorph's primary contract with everything that
consumes it — scripts, CI jobs, agents (design principle §3: the text rendering
derives from it, never the reverse). The
single source of truth in code is `PlanRenderer.ToJsonModel`
(`src/Schemorph.Core/Planning/PlanRenderer.cs`); keep this document and that
method in sync. Every surface that embeds a plan — `diff --format json` output,
the `plan` property of the `apply --format json` envelope — serializes this exact
shape.

## Versioning

`formatVersion` follows Terraform's machine-readable-format convention, and is
independent of the product version:

- **Minor** increments (`1.1`, `1.2`, ...) are backward-compatible additions.
  Consumers MUST ignore object properties they do not recognize.
- **Major** increments are breaking changes to existing properties. These are
  rare and deliberate.

Current version: **`1.6`**.

| Version | Change |
|---|---|
| `1.0` | Initial stable shape: `changes[]` with per-change `actions` lists |
| `1.1` | Added `planHash` (additive) — the apply-gate fingerprint |
| `1.2` | `explanation` populated on every change; `sql` populated on `redefine` changes (the exact idempotent script) and on declarative changes whose slice of the update script is attributable (additive — both fields were reserved as `null` since 1.0) |
| `1.3` | Added `atomicity` (additive) — the apply guarantee the provider declares ([ADR-0004 addendum](adr/0004-failure-semantics-and-resume.md)). Excluded from `planHash`, so a hash reviewed under 1.2 still gates a 1.3 apply |
| `1.4` | `planHash` now binds the **executed script text** — the declarative update script the apply runs, plus each change's `sql` — in addition to the action shape. Bugfix: the action tuples are an object-level summary, so two plans that touch the same objects with the same operations but different DDL (a column added vs. only a constraint re-added) used to share a hash; the gate now tells them apart. No JSON field changed — the plan document is byte-identical to 1.3 — but the same plan hashes to a new value, so a hash captured under ≤1.3 no longer matches (it fails closed: the apply refuses rather than running unreviewed DDL). The update-script text itself is not embedded in the JSON; reviewers read it via the review script / `diff --format sql` |
| `1.5` | `changes[].sql` is populated on **every** provider's declarative changes (additive — the field was reserved as `null` since 1.0 and stayed null wherever a provider had no attribution). Two consequences worth stating: the safety lint that reads those slices (`SCHEMORPH101`, `SCHEMORPH102`) now has something to read on plans where it previously had nothing, so a plan can carry warnings it did not carry before; and **every** plan hashes to a new value — partly because `planHash` binds each change's `sql` (since 1.4) and the field went from `null` to text, and partly because the fingerprint's own parts are now delimited by characters its inputs cannot contain, where before two of the delimiters (a pipe, a newline) were characters SQL text can hold. A hash captured earlier fails closed — the apply refuses rather than running unreviewed DDL. No JSON field changed either way: `planHash` is opaque to consumers, who compare it and pass it back rather than recompute it |
| `1.6` | Adds `excluded[]` — objects the engine's update script contains statements for that the plan does not execute (Schemorph's own history ledger, and destructive changes gated out of the plan). Purely additive: the field is always present, empty when the plan runs everything its script contains, and **`planHash` is unchanged** — it is derived from the actions and script the hash already binds, and a plan's identity must not move because it started explaining itself better. A hash captured under 1.5 still matches. Consumers that treated a script statement as proof of execution should read this field: the review document now names the same objects above the script |

## Shape

```json
{
  "formatVersion": "1.6",
  "planHash": "bd270dd7f6ba…(64 hex)",
  "atomicity": "partial",
  "hasChanges": true,
  "hasDestructiveChanges": false,
  "changes": [
    {
      "objectName": "dbo.Category",
      "objectType": "Table",
      "actions": ["alter"],
      "risk": "warning",
      "sql": "ALTER TABLE [dbo].[Category]\n    ADD [Slug] NVARCHAR (100) NULL;",
      "explanation": "The live definition differs from the desired state; altered in place by the declarative publish."
    },
    {
      "objectName": "dbo.CategoryFullView",
      "objectType": "View",
      "actions": ["redefine"],
      "risk": "safe",
      "sql": "CREATE OR ALTER VIEW dbo.CategoryFullView AS …",
      "explanation": "The file's checksum differs from the last applied definition; re-defined idempotently (CREATE OR ALTER)."
    }
  ],
  "messages": [
    { "severity": "Warning", "code": "SCHEMORPH005", "text": "Skipped ..." }
  ]
}
```

## Fields

| Field | Type | Meaning |
|---|---|---|
| `formatVersion` | string | Format version (see Versioning above) |
| `planHash` | string | SHA-256 fingerprint of exactly what would execute: each change's name, type, actions and risk in plan order, **plus the executed script** — the declarative update script and each change's `sql`, whatever its kind (since 1.4). Messages, `explanation` and `atomicity` are excluded (they describe a plan, they are not what it runs). Pass to `apply --expect-plan <hash>` (or MCP `schemorph_apply.expectedPlanHash`) to guarantee the apply runs the reviewed plan or refuses (`plan_mismatch`) |
| `atomicity` | string | What an apply of this plan guarantees on partial failure: `partial` (stages commit independently; a failure leaves earlier stages applied — SQL Server's mode) or `transactional` (the apply lands whole or not at all; only claimed where the tool owns the transaction boundary). See [failure-semantics.md](failure-semantics.md). Excluded from `planHash` |
| `hasChanges` | bool | `true` when `changes` is non-empty; pairs with exit code 2 on `diff` |
| `hasDestructiveChanges` | bool | `true` when any change carries `risk: "destructive"` |
| `changes` | array | One entry per planned change, in plan order (redefines last — they execute after the declarative publish) |
| `changes[].objectName` | string | Fully qualified object name (`schema.object`) |
| `changes[].objectType` | string | Provider-raw object type (`Table`, `View`, `Procedure`, ...) |
| `changes[].actions` | string[] | What will be done, in order. Today always one verb; composite operations (e.g. a rebuild = `["drop", "create"]`) become expressible without a breaking change |
| `changes[].risk` | string | `safe` \| `warning` \| `destructive` (design principle §4: destructive = the change loses data that cannot be recomputed — a DROP of an object that holds data, or an ALTER that removes a column the desired state no longer declares) |
| `changes[].sql` | string? | The SQL this change will execute. On `redefine` changes: the exact idempotent script, verbatim. On declarative changes: this change's slice of the update script. How the slice is obtained is the provider's business and differs by engine — where the script comes from a generator it is attributed from that generator's own per-object markers (or, when the work is announced under a dependent object the generator names in its own right — a check, default, or foreign-key constraint — from the table the segment's `ALTER TABLE` statements target); where the provider synthesizes the script itself, each statement records the object it belongs to as it is emitted, so attribution is exact. `null` whenever attribution is not certain: an unreadable segment, statements spanning more than one object, or a target the comparison did not report. A missing slice is honest, a wrong one is not. What executes on the declarative path is always the whole publish, not these slices — but they are **bound by `planHash`** (since 1.4), so a slice that changes invalidates a reviewed hash |
| `changes[].explanation` | string? | Deterministic rationale for the change: why it is planned and how it will be performed (e.g. checksum-difference reasoning on redefines, data-loss statement on destructive drops). Descriptive only: excluded from `planHash` |
| `messages` | array | Diagnostics attached to the plan (gated-out destructive changes, skipped non-model files, engine warnings) — see [errors.md § Provider messages](errors.md#provider-messages) |
| `excluded` | array | Objects the update script has statements for that this plan will **not** execute (since 1.6). Distinct from an apply's excluded changes: those are changes the plan held and the run left out, whereas these never became actions. Present and empty when there are none, so "nothing excluded" is distinguishable from "this version does not report it". Excluded from `planHash` |
| `excluded[].objectName` | string | Fully qualified object name (`schema.object`) |
| `excluded[].reason` | string | Why it does not execute, in the reviewer's terms — what the tool does with the object, not which branch dropped it |

## Action verbs

| Verb | Meaning |
|---|---|
| `create` | Object will be created (declarative path) |
| `alter` | Object will be altered in place (declarative path) |
| `drop` | Object will be dropped (declarative path; data-holding drops are gated behind `--allow-destructive`) |
| `redefine` | Programmable object will be idempotently re-defined (`CREATE OR ALTER`, ADR-0002 strategy 2) |

New verbs may appear in minor versions; consumers should treat an unknown verb as
"a change they cannot classify", not an error.

## Relationship to exit codes

`diff` exits `2` when `hasChanges` is true, `0` when false, `1` on error (see
[errors.md](errors.md)). Agents can branch on the exit code without parsing, or
parse the plan for the detail.
