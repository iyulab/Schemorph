# Spike: parser-backed schema rewriting for the shadow harness

Throwaway code; the deliverable is the measurement, recorded in
[ADR-0007's addendum](../../docs/adr/0007-postgres-engine-selection.md).

## Question

ADR-0007 deferred a parser binding ("not adopted now") while requiring the
scratch-schema shadow variant's rewrite step to "become parser-based" — string
substitution was already refuted by measurement (`pg_get_indexdef` emits
fold-safe schema qualifiers *unquoted*, so quoted-form substitution misses them
and retargets nothing; cycle-76). P1 needs the answer: which parser, and which
rewrite mechanism?

## Candidates

- **`pgsqlparser` 1.0.0** (mysticmind/pgsqlparser-dotnet, MIT) — libpg_query
  wrapper, protobuf AST, deparse. Measured here.
- `Npgquery` — no releases, no macOS native; excluded (Schemorph ships osx-arm64).
- Vendored P/Invoke over pinned libpg_query — the fallback, not the first choice
  (three functions of surface, but native builds per RID become ours to own).

## Measurements (win-x64, 2026-07-22)

| Axis | Result |
| --- | --- |
| Parse of quoted PascalCase multi-statement DDL | ✓ 3 statements, `stmt_location`/`stmt_len` present |
| Grammar version | 170005 (PostgreSQL 17 — covers the PG16 target) |
| Schema names as *structured fields* | ✓ everywhere one can hide: `RangeVar.schemaname` (table, index ON, ALTER), FK `pktable.schemaname`, qualified `TypeName.names`, qualified `funcname` |
| Deparse round-trip preserves quoting | ✓ `"SrcTest"`, `"Tier"` intact — **the axis that eliminated psqldef** (its synthesis lost quoting) |
| Expression index (`lower("Tier")`) survives deparse | ✓ |
| Error reporting | ✓ message + `CursorPos` |
| Native RIDs in the package | win-x64, linux-x64, osx-arm64, osx-x64 ⊇ the release RIDs |

## Conclusion (carried into the ADR addendum)

Adopt `pgsqlparser` for the shadow harness: **AST rewrite + deparse** (set the
schema fields, deparse, execute against the scratch schema) rather than
offset-guided text surgery. Deparse normalizes formatting, and that is
acceptable *here specifically*: the shadow text is never compared as text —
both sides are read back from `pg_catalog` in the engine's canonical rendering,
so only the structures matter. Verbatim rendering remains the rule where text
IS the artifact (inspect output, redefine scripts).

Risks, stated: single-maintainer binding, one release. Mitigated the same way
DacFx is: exact version pin, upgrades gated on the corpus. If the binding goes
unmaintained, the fallback above replaces a three-function surface.
