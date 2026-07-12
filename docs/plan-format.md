# The plan format

The machine-readable plan is Schemorph's primary contract with agents and scripts
(design principle §3: the text rendering derives from it, never the reverse). The
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

Current version: **`1.1`**.

| Version | Change |
|---|---|
| `1.0` | Initial stable shape: `changes[]` with per-change `actions` lists |
| `1.1` | Added `planHash` (additive) — the apply-gate fingerprint |

## Shape

```json
{
  "formatVersion": "1.1",
  "planHash": "bd270dd7f6ba…(64 hex)",
  "hasChanges": true,
  "hasDestructiveChanges": false,
  "changes": [
    {
      "objectName": "dbo.Category",
      "objectType": "Table",
      "actions": ["alter"],
      "risk": "warning",
      "sql": null,
      "explanation": null
    },
    {
      "objectName": "dbo.CategoryFullView",
      "objectType": "View",
      "actions": ["redefine"],
      "risk": "safe",
      "sql": null,
      "explanation": null
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
| `planHash` | string | SHA-256 fingerprint of exactly what would execute (each change's name, type, actions, risk, in plan order; messages and descriptive fields excluded). Pass to `apply --expect-plan <hash>` (or MCP `schemorph_apply.expectedPlanHash`) to guarantee the apply runs the reviewed plan or refuses (`plan_mismatch`) |
| `hasChanges` | bool | `true` when `changes` is non-empty; pairs with exit code 2 on `diff` |
| `hasDestructiveChanges` | bool | `true` when any change carries `risk: "destructive"` |
| `changes` | array | One entry per planned change, in plan order (redefines last — they execute after the declarative publish) |
| `changes[].objectName` | string | Fully qualified object name (`schema.object`) |
| `changes[].objectType` | string | Provider-raw object type (`Table`, `View`, `Procedure`, ...) |
| `changes[].actions` | string[] | What will be done, in order. Today always one verb; composite operations (e.g. a rebuild = `["drop", "create"]`) become expressible without a breaking change |
| `changes[].risk` | string | `safe` \| `warning` \| `destructive` (design principle §4: destructive = a DROP of anything holding data) |
| `changes[].sql` | string? | Reserved: per-change SQL (Phase 2 plan explanations); `null` until populated |
| `changes[].explanation` | string? | Reserved: human-readable rationale; `null` until populated |
| `messages` | array | Diagnostics attached to the plan (gated-out destructive changes, skipped non-model files, engine warnings) — see [errors.md § Provider messages](errors.md#provider-messages) |

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
