# The desired-state SQL format

`inspect --out <dir>` renders a live database into a tree of desired-state `.sql`
files, and the MCP resource `schemorph://schema` renders the identical tree as one
concatenated document. Both surfaces call the same per-provider renderer
(`DesiredStateRenderer.Render` for Postgres, `SqlServerProvider`'s internal
`RenderDesiredState` for SQL Server) — this document and those methods must stay in
sync, the same pact `docs/plan-format.md` keeps with `PlanRenderer`.

This is the contract a consumer running Schemorph alongside an ORM needs to answer
"does my ORM's model match the deployed schema": inspect the live database, diff the
result against your own copy of the desired-state files (or run `schemorph diff`
directly against a schema directory your ORM's model produces), and a difference
means the schema drifted. That comparison is only trustworthy if the *rendering
rules* — not just the schema — are pinned, which is what this document versions.

**Scope**: this governs `inspect`/`schemorph://schema` **output** only. A `--schema`
directory you hand-author or generate yourself (`diff`/`apply`'s input) is not bound
by it — [architecture.md](architecture.md#repository-layout-users-project) already
says that layout is convention, not enforcement, and stays that way.

## Versioning

`desiredStateFormatVersion`, reported on the CLI manifest (`schemorph schema` →
`desiredStateFormatVersion`, alongside `planFormatVersion`), follows the same
convention as the plan format:

- **Minor** increments (`1.1`, `1.2`, ...) are backward-compatible additions to the
  contract below (e.g. a new object kind, an added directory).
- **Major** increments are breaking changes to an existing rule (e.g. renaming the
  sanitization scheme, changing how constraints are attached). These are rare and
  deliberate — a consumer with an automated byte-diff drift check needs to know
  before upgrading past one, not after a false "drift" report.

Independent of the product version and of `planFormatVersion` — a release can change
one contract without touching the other.

Current version: **`1.0`**.

| Version | Change |
|---|---|
| `1.0` | Initial documented contract (below) |

## Layout

One file per database object, grouped into kind subdirectories:

```
tables/dbo.Orders.sql
views/dbo.OrderSummary.sql
procedures/dbo.usp_CloseOrder.sql
functions/dbo.fn_OrderTotal.sql
triggers/dbo.trg_Orders_Audit.sql
```

Both providers use the same five kinds — `tables`, `views`, `procedures`,
`functions`, `triggers` — matching the MCP resource's `schemorph://schema/{kind}/{name}`
path parameter. A provider that does not support a kind (declared on the CLI
manifest's `provider.capabilities`) never emits that subdirectory.

## Naming

The relative path is `{kind}/{schema}.{object}.sql` (Postgres quotes each segment
per its own identifier rules; SQL Server does not). An object name that is not a
usable file-name segment — containing a character no common file system accepts —
is sanitized: offending characters become `_`, and a short content hash of the
*original* name is appended, so two differently-named objects can never collapse
onto the same file (`DesiredStateFile.SafeSegment` is the canonical implementation).
An already-clean name passes through verbatim.

## Statement shape

- Each file is a **complete, self-applicable desired state** for its object:
  constraints and indexes are folded into their owning table's file rather than
  emitted as separate top-level elements, even where the source catalog models
  them separately (DacFx) or as separate rows (Postgres' `pg_constraint`/`pg_indexes`).
- Every identifier is quoted (Postgres: double-quoted, case-preserving — an unquoted
  PascalCase identifier folds to lower case on re-apply, which does not round-trip).
- Every statement ends with `;` followed by a newline. No provider-specific batch
  separator (SQL Server's `GO`) appears in Postgres output, or vice versa.
- Defaults and nullability are rendered verbatim from the catalog — the file is a
  direct transcription, not a normalized or reformatted one.

## Stability

The renderer itself is a pure function of the object list it is given — the same
input list produces byte-identical output on every call (`Rendering_the_same_table_twice_is_byte_identical`
in `DesiredStateRendererTests`, Postgres side). What that does **not** yet cover:
whether each provider's own catalog query returns objects (and, within one object,
its constraints/indexes) in a stable order run over run. Neither provider's inspect
query currently declares an explicit deterministic order (Postgres: no `ORDER BY`
in `PostgresProvider`'s catalog queries; SQL Server: DacFx's `TSqlModel.GetObjects`
enumeration order is not documented as stable). In practice this only matters to a
byte-diff-based drift check if it diffs raw file content rather than normalizing
first (e.g. via `schemorph diff`, which compares parsed models, not text) — but it
is a real gap in the letter of "round-trip stable," tracked as a follow-up hardening
pass rather than closed by this version.

## Relationship to `schemorph diff`

`inspect --out` and `schemorph://schema` are conveniences for reading the live
schema as text — `schemorph diff --schema <dir>` is the actual consistency check
itself, and does not require literally re-writing an ORM's model as `.sql` files
first: it compares the desired-state directory you already maintain against the
live database's current definitions and reports a plan, empty when nothing has
drifted. This format governs the *shape* of files either side of that comparison
was produced from — not the comparison itself. For the comparison itself, see the
[ORM/schema drift recipe](recipes/orm-schema-drift-check.md).
