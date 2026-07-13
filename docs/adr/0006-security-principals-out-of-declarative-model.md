# ADR-0006: Security principals are out of the declarative model (SQL Server)

- **Status:** Accepted
- **Date:** 2026-07

## Context

[ADR-0002](./0002-hybrid-object-strategy-model.md) routes each object class to a mechanism and explicitly defers the classification of edge-case object types — "sequences, user-defined types, synonyms, **permissions**, ..." — to be "decided per-provider, and recorded as they are made." This ADR records the decision for **security principals** (database users, logins, database/application/server roles, role membership, permissions), forced by real consumer evidence.

The second dogfooding round (a 41-table `m3l → mdd-booster → Schemorph` consumer, 2026-07-13) ran `diff` against a live database whose desired-state was a code-generator's output. DacFx reported, as part of an ordinary column edit's plan:

- `DROP USER [app-login]` — the application's own database login
- `DROP USER [NT AUTHORITY\SYSTEM]` — the backup-operator account

classified `risk: warning` with top-level `hasDestructiveChanges: false`. Because those drops are not "a DROP of anything holding data" ([Design Principle §4](../design-principles.md)), they were **not** gated as destructive — so a default `apply` (no `--allow-destructive`) would have executed them, deleting the app's login as a side effect of an innocuous schema change. DacFx had additionally merged the two `DROP USER` statements into the update-script batch of an unrelated table rebuild.

The root cause is a category error: principals were being treated as declarative structural state ("absent from the desired-state files ⇒ drop"). But a code-generated desired-state never emits principals — they are provisioned and managed outside the schema model (server setup, security administration). Nothing in the model implies they should cease to exist.

## Decision

**Security principals are excluded from the SQL Server declarative diff by default.** The provider adds these DacFx `ObjectType`s to `DacDeployOptions.ExcludeObjectTypes` for every compare and apply:

`Users, Logins, LinkedServerLogins, DatabaseRoles, ApplicationRoles, ServerRoles, RoleMembership, ServerRoleMembership, Permissions`.

Consequently Schemorph neither creates nor drops principals: they are neither added from, nor removed for being absent from, the desired state.

This is consistent with the three-strategy model of ADR-0002: principals are not structural state (tables/columns/indexes/constraints), not programmable objects, and not versioned data. They are a fourth class the declarative strategy does not own.

## Consequences

**Positive**

- Design Principle §4 is honored where it mattered most: an application login can no longer be silently dropped by a routine schema change. The "DROP USER folded into a table rebuild" hazard disappears at the source.
- The code-generator consumption path (m3l → mdd-booster → Schemorph), where principals are structurally out-of-model, works without false drops.
- No configuration surface is introduced — the safe behavior is the default, matching Design Principle §4 ("safe by default").

**Negative / accepted risks**

- A consumer who *wants* to manage principals declaratively (hand-authored `CREATE ROLE`/`CREATE USER` in the schema directory) is not served today: those statements are ignored rather than applied. This is the deliberately conservative default. Managing principals declaratively becomes an **opt-in** — a configuration surface added when a real consumer needs it (demand-driven, per the roadmap's config-surface deferral), not speculatively now.
- Excluding an object type is coarse: it turns off both add and drop for principals uniformly. A finer "create but never drop" policy is possible later if demand distinguishes the two.

## Alternatives considered

- **Classify principal DROPs as destructive** (so the existing gate catches them). Rejected as the primary fix: it only defends `apply` (a `diff` still shows the phantom drop as pending drift, and `--allow-destructive` would still execute it), it stretches §4's definition of destructive ("DROP of anything holding data") to cover data-less objects — a change to a design anchor that would itself require an ADR — and it leaves the category error (principals-as-declarative-state) in place. Excluding principals removes the drift at the source, so there is nothing left to misclassify.
- **A `schemorph.json` scope/exclusion policy** as the delivery vehicle. Rejected for now as premature: the *safe default* needs no configuration, and introducing a config surface is demand-driven (roadmap Phase 3). The opt-in to manage principals declaratively is the future config, not the fix.
