# ADR-0008: The interface anchor names a property, not an audience

- **Status:** Accepted
- **Date:** 2026-07

## Context

[Design Principle §3](../design-principles.md) was written as *"AI agents are a primary user"*, and the README carried that framing outward: a tagline ("built for the AI-agent era"), a section titled "Built for AI Agents", and a comparison-table column named "AI-agent-native output".

Everything underneath that heading, however, describes the **interface**, not the caller. Structured output everywhere, plans before actions, semantics in exit codes, explainability, an MCP surface — each of those properties holds identically for a shell script, a CI job, a `Makefile`, and a person reading `diff --format sql` before signing off on a release. None of them is true *because* the caller reasons.

Naming an audience in an anchor costs three things:

- **It dates the anchor.** Anchors are meant to outlive implementation vocabulary. "Agent" is vocabulary.
- **It misdirects the human reader**, who is most of the callers of a CLI one types into. A person arriving at the README was told, in the first line, that the tool was built for someone else.
- **It made one comparison claim unfalsifiable.** "AI-agent-native output" is a bar this project defined, and competitors were scored ❌ against it. The properties that are actually verifiable — a versioned plan contract, an apply that executes only a reviewed plan fingerprint — were the evidence given for the claim, so they should be the claim.

## Decision

**Design Principle §3 is restated as a property of the interface: *Every interface is programmable*.** The same information is available to a person reading a terminal and to a program parsing a stream, with the structured form authoritative. Its bullets are unchanged in substance.

The anchor gains an explicit second direction, which the audience framing had let slip: a machine-readable surface is never a reason to ship output a person cannot review. `diff --format sql` renders the same plan as a sign-off document, and that path is documented as a first-class workflow rather than as a footnote under a machine-facing heading.

Public documentation follows:

- The README tagline states what the tool does, not who it was built for.
- The comparison column becomes a checkable property (a reviewable plan contract plus a gated apply) instead of a self-defined "agent-native" bar.
- The single "Built for AI Agents" section splits in two: the human review workflow first, the machine-facing surface second.

**No capability changes, and nothing is renamed out of existence.** The MCP server, the versioned plan format, the typed error envelope, the CLI manifest, and the packaged Agent Skill are shipped features; they keep their names and their documentation, because that is what they are. What is retired is the *positioning* — the claim that one class of caller is the point of the project.

Earlier ADRs are dated records and are not edited. ADR-0001's rationale refers to a "plan-centric, agent-first design"; the decision it records (build on DacFx in-process, keep structured comparison results) stands unchanged, and only its framing is superseded here.

## Consequences

**Positive**

- The anchor stops depending on a word whose meaning shifts, and now says something falsifiable about the code: structured form authoritative, human rendering derived.
- The reviewer path (`diff --format sql` → sign-off → `apply --expect-plan`) becomes visible in the README instead of being buried as one bullet in a machine-facing section — it was always the safest way to use the tool.
- Comparison claims narrow to properties that can be re-verified against the other tools' documentation.

**Negative / accepted risks**

- The project's one-line positioning loses a high-traffic discovery keyword. Accepted: `schemorph mcp` and the Agent Skill are still named and documented, so a reader looking for that capability finds it one section down instead of in the tagline.
- Two framings coexist in the repository for as long as ADRs 0001–0007 stand, which is intended. A reader who diffs the anchor against ADR-0001's wording needs this ADR to reconcile them.

**Non-consequence**

- No behavior, flag, output shape, or exit code changes. This is a documentation and framing decision only.

## Alternatives considered

- **Leave §3 as it is and only reword the README.** Rejected: the README derives its framing from the anchors, so the phrasing would return at the next documentation pass. The anchor is the source.
- **Remove every mention of agents from the documentation.** Rejected: the MCP server and the Agent Skill are real, shipped, and named after what they are. Describing them without saying so makes the documentation worse, not more neutral.
- **Broaden the comparison column to "machine-readable output" and keep the ❌ marks.** Rejected: several of those tools do emit machine-consumable artifacts (deployment reports, generated SQL scripts), so the ❌ would be wrong under the broader name. Narrowing the column to the reviewable-plan-plus-gate property keeps the row honest.
