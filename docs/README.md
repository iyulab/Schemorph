# Documentation

Start from the question, not the filename.

## Using it

| Your question | Where it is answered |
|---|---|
| What does this tool do, and how do I install it? | [README](../README.md) |
| Which engines are supported, and how complete is each? | [README § Database Support](../README.md#database-support) — and [limitations](limitations.md) for the parts that are deliberately absent |
| **How do I undo a change I already applied?** | [ADR-0005](adr/0005-rollback-semantics.md) — roll forward for structure; down-migrations are deliberately not supported, and the reasoning is there |
| The apply failed partway through. What state is the database in? | [failure semantics](failure-semantics.md) |
| Why did it refuse to run, and what does this exit code mean? | [errors and exit codes](errors.md) — including the `SCHEMORPH1xx` safety-lint band |
| Why is it planning to drop something I did not ask it to drop? | [limitations](limitations.md) — start with the rename entry, which is the most common surprise |
| How do I make a person sign off before anything runs? | [recipe: human approval gate](recipes/human-approval-gate.md) — `--expect-plan` makes the reviewed document binding |
| How do I show the plan on a pull request? | [recipe: plan as a PR comment](recipes/github-actions-plan-comment.md) |

## Building on it

| Your question | Where it is answered |
|---|---|
| What is in the plan JSON, and what may change without warning? | [plan format](plan-format.md) — versioned, additive-only |
| What does the CLI expose, and how do I discover it from a script? | `schemorph schema` prints the manifest; [errors](errors.md) covers the envelope |
| How is the codebase arranged, and where does a provider end? | [architecture](architecture.md) |
| What is fixed about this project, and what is still open? | [design principles](design-principles.md) — changing that page requires an ADR |
| How do I run the tests, including the ones that need a live database? | [CONTRIBUTING § Running the live tests](../CONTRIBUTING.md#running-the-live-tests) |

## Decisions

ADRs record **why**, including the things deliberately not done. Newest last.

| | Decision |
|---|---|
| [0001](adr/0001-csharp-dacfx-foundation.md) | C# and DacFx as the foundation |
| [0002](adr/0002-hybrid-object-strategy-model.md) | Three object strategies — declarative, re-definition, versioned migration |
| [0003](adr/0003-postgres-as-second-provider.md) | PostgreSQL as the second provider, and what a provider boundary must survive |
| [0004](adr/0004-failure-semantics-and-resume.md) | What an interrupted apply leaves behind, and how a re-run resumes |
| [0005](adr/0005-rollback-semantics.md) | Rollback per strategy — and why there is no `undo` verb |
| [0006](adr/0006-security-principals-out-of-declarative-model.md) | Security principals stay outside the declarative model |
| [0007](adr/0007-postgres-engine-selection.md) | How the PostgreSQL comparison is built |
| [0008](adr/0008-interface-anchor-names-a-property.md) | What the project is positioned as, and what that stopped claiming |

---

This page is a map, not a source: every fact lives in the document it points at. If
an answer here disagrees with the page it links to, the page is right.
