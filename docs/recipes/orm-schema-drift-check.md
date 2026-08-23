# Recipe: catching ORM/schema drift

A team that runs an ORM (EF Core, ActiveRecord, whatever) purely as a mapper —
migrations off, schema ownership moved to Schemorph — has one recurring question:
*does my ORM's model still match what is actually deployed?* Left unanswered, a
mismatch surfaces at runtime as a missing column or a type error, from whichever
request happens to touch it first.

The check itself is one command. Nothing new to learn — it is `schemorph diff`,
pointed at whatever directory your ORM's model corresponds to:

```bash
schemorph diff --schema ./schema --format json
# exit 0, hasChanges:false  → the deployed schema matches ./schema
# exit 2, hasChanges:true   → it does not — read the plan for what moved
```

Which `--schema` directory to point it at depends on how your model gets to SQL.

## If your generator already emits desired-state SQL

The simplest case, and the one this recipe is really about: something in your
pipeline — a code generator, a migration-authoring step, a hand-maintained
directory — already produces a Schemorph-shaped `--schema` tree alongside (or
instead of) the ORM's own model. Run the command above against it, in CI, on a
schedule or on every deploy. Empty diff is the whole contract. This is the setup
[the plan-on-PR](github-actions-plan-comment.md) and
[approval-gate](human-approval-gate.md) recipes already assume — drift-checking is
the read-only half of the same loop, `diff` without ever reaching `apply`.

## If you only have the ORM's model, not a `--schema` tree

When nothing in your pipeline renders the ORM's model as desired-state SQL, inspect
the *live* database instead and compare that against what your ORM asserts:

```bash
schemorph inspect --url "$SCHEMORPH_URL" --out ./live-schema
```

(`schemorph mcp`'s `schemorph://schema` resource renders the identical tree for an
agent host that would rather read it as MCP context than shell out to the CLI.)

Whatever you diff `./live-schema` against — a hand-rolled comparison, a generator's
own "expected schema" fixture — you are now trusting that Schemorph's *rendering
rules* did not change between the run that produced your reference copy and the run
that produced `./live-schema`. That is exactly what
[the desired-state format](../desired-state-format.md) versions:

```bash
schemorph schema | jq -r .desiredStateFormatVersion
```

Pin the value your reference copy was generated under, and compare it before
trusting a text diff — a version bump means the *rendering* moved, which reads as
schema drift to a byte-diff but is not.

## Why `diff` is still the better answer when it is available

A byte-diff against `inspect` output tells you the two are different; it does not
tell you *why*, or whether the difference is destructive. `schemorph diff` computes
an actual plan — with `risk`, `explanation`, and the exact `sql` — because it
compares parsed models under [the plan format](../plan-format.md), not text. Reach
for the `inspect`-and-compare path in the previous section only when a proper
`--schema` tree genuinely is not available; prefer `diff` whenever it is.

## Pairing it with CI

| Stage | Command | Purpose |
|---|---|---|
| Every PR touching the ORM model | regenerate `--schema` (if generated) or refresh the reference `inspect` copy | keep the comparison target current |
| Scheduled / pre-deploy | `schemorph diff --schema ./schema --format json` (or the `inspect`-and-compare path) | catch drift before a request does |
| On a `desiredStateFormatVersion` bump | re-baseline the reference copy, note it in the change that bumps the Schemorph dependency | avoids a false "drift" report from a Schemorph upgrade alone |

If `diff` reports real drift, the fix is a normal `apply` (see
[the human approval gate](human-approval-gate.md) if that needs sign-off first) —
this recipe is about noticing, not about remediation.
