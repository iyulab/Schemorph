# Candidate A — psqldef 3.11.16 (22f477b), run 2026-07-21

Binary: `psqldef_windows_amd64.zip` from the v3.11.16 release (not committed).
Connection: `-U spike_app -h 127.0.0.1 -p 15544 spike_target` (NOSUPERUSER role), desired via `--file`.

## Step outputs (verbatim, trimmed to the decisive lines)

**(1) empty DB, v1 `--dry-run`** — full CREATE script for all 4 tables, wrapped in
`BEGIN; … COMMIT;`. Quoted identifiers preserved in CREATE TABLE output.

**(2) v1 `--apply`** — succeeded, same script.

**(3) v1 re-`--dry-run`** — `-- Nothing is modified --` ✅
The desired text says `"Status" IN ('active', 'suspended')`; the catalog stores
`= ANY (ARRAY[...])`. psqldef's *comparison* normalizes across that gap.

**(4a) v2 `--dry-run`** — three ALTERs as expected, but note the CHECK synthesis:

```
ALTER TABLE "vibebase_control"."Workspaces" ADD COLUMN "Tier" text NOT NULL DEFAULT 'free';
ALTER TABLE "vibebase_control"."Workspaces" ADD CONSTRAINT "CK_Workspaces_Tier" CHECK (Tier = ANY (ARRAY['free', 'pro']));
ALTER TABLE "vibebase_control"."Resources" ADD CONSTRAINT "UQ_Resources_App_ExternalRef" UNIQUE ("AppId", "ExternalRef");
```

`Tier` inside the CHECK expression has **lost its quoting**.

**(4b) v2 `--apply`** — **FAILED on psqldef's own generated DDL**:

```
ALTER TABLE "vibebase_control"."Workspaces" ADD CONSTRAINT "CK_Workspaces_Tier" CHECK (Tier = ANY (ARRAY['free', 'pro']));
ROLLBACK;
2026/07/21 18:42:22 pq: column "tier" does not exist (42703)
```

Unquoted `Tier` case-folds to `tier`. Any schema with quoted mixed-case
identifiers referenced in CHECK expressions — which is exactly what EF Core-style
PascalCase schemas are, including the committed consumer's real control-plane
schema — cannot take this path.

**Rollback check** — `information_schema.columns` shows 4 columns, no `Tier`:
the failed batch rolled back *whole*, including the ADD COLUMN that had already
succeeded inside the transaction. The `BEGIN;…COMMIT;` wrapping is a real
atomicity guarantee, not cosmetic.

**(4c′) v2 re-`--dry-run` after applying the v2 delta manually with correct
quoting** — `-- Nothing is modified --` ✅ (comparison side survives the
`IN` → `= ANY(ARRAY)` round-trip even when the constraint entered the catalog
by another tool's hand).

**(5′) v1 desired against v2 database, no `--enable-drop`** —

```
ALTER TABLE "vibebase_control"."Resources" DROP CONSTRAINT "UQ_Resources_App_ExternalRef";
-- Skipped: ALTER TABLE "vibebase_control"."Workspaces" DROP COLUMN "Tier";
```

`DROP COLUMN` is gated (skipped, destructive-by-default philosophy compatible
with Schemorph §4). **`DROP CONSTRAINT` is not gated** — a UNIQUE constraint
would be dropped without any explicit opt-in. Schemorph classifies constraint
removal as a plan change that must at minimum be visible and attributable;
gate-free silent constraint drops would need wrapping-level policy on top.

## Axis observations

- **R1** — entire run as `NOSUPERUSER`, no extensions touched. Pass.
- **R5** — transactional apply confirmed by measured rollback. But the boundary is
  **owned by psqldef**: Schemorph could not join its own ledger write or
  planHash re-verification into that transaction, and `--disable-ddl-transaction`
  is the only control exposed. Guarantee: yes. Control: no.
- **R3** — split verdict. *Comparison* normalization: pass (3, 4c′).
  *Synthesis*: fail — quoting loss in CHECK expressions is a correctness bug on
  the acceptance schema itself (4b). Upstream-fixable in principle (minimal repro
  captured here; filing it is a human decision — sqldef is third-party OSS), but
  the engine's diff-to-DDL layer is exactly the part a wrapper cannot reach into.
- **Distribution** — one ~10 MB Go binary per RID, shipped inside a dotnet tool;
  version pinning and process management become ours.
- **S2** — `--dry-run` output is a deterministic, executable script (good planHash
  raw material), but it is psqldef's rendering, not ours; review-script parity with
  the SQL Server provider would be constrained to what psqldef prints.
