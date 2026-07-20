# ADR-0068: Source-generated binary COPY and staged upsert for the AOT SQL tier

- Status: Proposed
- Date: 2026-07-20
- Related: [ADR-0058](0058-aot-sql-row-mapping.md) (the "considered future addition" this executes;
  generated mapper mechanics), [ADR-0051](0051-postgresql-bulk-insert.md) (the EF-tier COPY seam whose
  semantics and vocabulary this mirrors, and the raw-parity benchmark discipline),
  [ADR-0060](0060-database-neutral-migration-core.md) (the per-provider package split this follows),
  [ADR-0006](0006-incremental-source-generator-conventions.md) (generator conventions).

## Context

ADR-0058 shipped the AOT SQL tier with `InsertManyAsync` as its batch ceiling: one reused prepared
command, one round trip per row, `sqlSuffix` for `ON CONFLICT …`. It deferred a real bulk path to its
own decision ("Considered future addition: source-generated binary COPY for the AOT tier"). Two things
now force that decision:

1. **The EF-tier COPY path is structurally unavailable to this tier.** `ExecuteInsertAsync`
   (ADR-0051) takes its entry point and column metadata from the EF model and compiles per-column
   writers at runtime via `System.Linq.Expressions` — the package is not `IsAotCompatible`. An EF-free
   NativeAOT host has no binary-COPY option at any price today.
2. **A concrete downstream workload shape needs it: dirty-flag-and-sweep persistence.** A simulation
   loop keeps hot per-entity state in memory (typically in an actor), marks entities dirty on update
   (a field write, zero allocation), and periodically sweeps dirty entities into one batched upsert —
   the loss-tolerant, latest-wins persistence shape now documented in the data-rate-shaping
   capability. The sweep workload is *upsert-shaped* (the rows usually exist), arrives in bursts of
   thousands to tens of thousands of rows per flush, and lives in exactly the low-allocation AOT hosts
   this tier serves. At those sizes a per-row round-trip loop is the bottleneck; PostgreSQL's native
   ingestion path (binary `COPY … FROM STDIN`) is roughly an order of magnitude faster (ADR-0051
   measurements).

The generator groundwork already exists: `SqlRecordMapperGenerator` knows every column's name, CLR
type, and JSON/`Jsonb` handling; branches on the assembly-level `[UseElarionSql(Provider =
SqlProvider.Npgsql)]` trigger; emits a `Columns` class with `All`/`AllParameters`/`AllAssignments`
fragments; and supports `record struct` rows. Emitting per-column `NpgsqlBinaryImporter` writes is the
same compile-time, reflection-free shape as the existing `BindParameters` emit — "if it builds, it
maps."

What must *not* happen (AGENTS invariant): `InsertManyAsync` growing options until it is a second bulk
subsystem, or the EF COPY package being repackaged for this tier. The mechanisms genuinely differ
(runtime-compiled expression trees vs source generation), so this is two implementations by tier —
the same split ADR-0060 chose for migrations.

## Decision

### Capability interface, implemented by the generated partial

`Elarion.Sql.PostgreSql` (the existing Npgsql provider sibling) gains a static-abstract capability
contract, and the generator implements it on the `[SqlRecord]` partial **only** under the Npgsql
provider trigger:

```csharp
// Elarion.Sql.PostgreSql — the generated partial implements this when Provider = SqlProvider.Npgsql.
public interface INpgsqlCopyRecord<TSelf> where TSelf : INpgsqlCopyRecord<TSelf> {
    /// <summary>COPY … FROM STDIN (FORMAT BINARY) command text for the full insertable column list.</summary>
    static abstract string CopyCommandText { get; }

    /// <summary>Writes one row's columns to the importer, in CopyCommandText column order.</summary>
    static abstract Task WriteRowAsync(NpgsqlBinaryImporter importer, in TSelf row, CancellationToken ct);
}
```

The interface lives in the provider package, not `Elarion.Sql` core, because its signature names
Npgsql types — core stays provider-neutral, exactly as `ISqlRecord<T>` stays ADO-neutral. The
generated code compiles into the consuming assembly, which already references Npgsql under the Npgsql
trigger (the existing `Jsonb` parameter emit proves the pattern). Consequences of the split:

- **Fail-loud at compile time.** The public entry points constrain on
  `where T : ISqlRecord<T>, INpgsqlCopyRecord<T>`; calling COPY on a record compiled under
  `SqlProvider.Portable` is a missing-interface compile error, not a runtime probe.
- **AOT-clean by construction.** Per-column writes are emitted concrete calls
  (`importer.WriteAsync(row.X, NpgsqlDbType.Real, ct)` etc., `Jsonb` for JSON columns via the
  canonical serialization accessor) — no reflection, no expression compilation, no boxing for
  `record struct` rows (`in TSelf`).

### Public surface: two verbs on the session, EF-tier vocabulary

Extensions in `Elarion.Sql.PostgreSql`, on **`ISqlSession` only** (the no-raw-`DbConnection`-twin rule
from ADR-0058 holds — enlistment in the session's ambient transaction is non-negotiable):

```csharp
long written = await session.ExecuteInsertAsync(rows, ct);                  // straight COPY
long written = await session.ExecuteInsertAsync(rows, new SqlBulkInsertOptions {
    OnConflict = SqlBulkInsertConflictBehavior.Update,                      // …or DoNothing
    ConflictColumns = ["id"],                                               // column names; default: omitted → constraint inference
}, ct);
```

- The name mirrors the EF tier's `ExecuteInsertAsync` deliberately: same verb, same non-tracking
  set-based semantics, same conflict vocabulary (`None`/`DoNothing`/`Update`), so moving a workload
  between tiers is a receiver change, not a redesign. The options bag is tier-local
  (`SqlBulkInsertOptions`) because its members speak SQL (column names), not EF model properties.
- Input shapes: `IEnumerable<T>` and `IAsyncEnumerable<T>` (rows stream into COPY as produced), plus
  an `IReadOnlyList<T>` fast path that iterates by index — a swept, reused `List<T>` of struct rows
  flushes without a boxed enumerator.
- `OnConflict = None` (default) streams straight into the target table — fastest, all-or-nothing on
  any error. `DoNothing`/`Update` stage through a `TEMP` table (`CREATE TEMP TABLE … (LIKE target
  INCLUDING DEFAULTS) ON COMMIT DROP`), COPY into it, then merge with one
  `INSERT INTO target SELECT … FROM temp ON CONFLICT (…) DO UPDATE SET col = EXCLUDED.col, …`. The
  merge statement is composed once per (type, options) from the generated `Columns` fragments and
  cached — composition is string assembly over generated constants, never reflection.
- Everything runs on the session's connection inside its ambient transaction; a handler's unit of
  work contains the COPY like any other write. No second connection, no second data source.

`InsertManyAsync` stays exactly as it is: the small-batch convenience for tens-to-hundreds of rows and
for portable (non-Npgsql) assemblies. The docs draw the line by row count; neither API grows toward
the other.

### Generator changes

Follow ADR-0006 mechanics: the COPY emit is an additional section of the existing
`SqlRecordMapperGenerator` output under the Npgsql provider branch — same pipeline, same equatable
model (the column model already carries everything needed), so incrementality is unchanged. Column →
`NpgsqlDbType` mapping is decided at generation time from the CLR type (the same table
`BindParameters` implies today, made explicit); an unmappable column type is a new fail-loud
diagnostic at build time, tracked in `AnalyzerReleases.Unshipped.md`. Generated output stays
deterministic and byte-stable; the affected generator suite gains golden-output and
`GeneratorCacheAssert` coverage.

### Benchmark gate (the ADR-0051 discipline)

Ships only at measured parity with a hand-written `NpgsqlBinaryImporter` loop. The
`tests/Elarion.Benchmarks` suite gains:

- straight COPY vs raw importer vs `InsertManyAsync` at 1k / 10k / 100k rows;
- **upsert-shaped** runs — staged `Update` merge into a pre-populated table at 1k–50k rows — because
  the motivating sweep workload is an upsert into existing rows, and the temp-table staging cost is
  the number that decides whether the docs recommend it per flush interval;
- an allocation column: the flush path for `record struct` rows allocates at flush frequency
  (command, staging SQL), never per row.

## Consequences

- The AOT tier gets a real bulk path with the same ceiling as the EF tier, and the dirty-flag-and-sweep
  recipe's flush target stops being the tier's weakest link at scale.
- `Elarion.Sql.PostgreSql` becomes a runtime provider package, not just the migration provider — the
  package reference and capability docs must say so, and the data-rate-shaping/sql-mapping pages
  update their "no COPY on this tier" caveats to point here.
- Two COPY implementations exist by design (EF runtime-compiled, AOT source-generated). They share
  vocabulary and semantics but not code; a behavior fix in conflict handling must be checked against
  both. The shared-semantics tests are the guard.
- The capability interface is Npgsql-specific public API in a provider package. SQLite and future
  providers are unaffected: bulk ingestion is a per-provider capability, not a portable seam — a
  provider without a native bulk path simply doesn't get the verb (closest-semantics substitution is
  `InsertManyAsync`, documented, per the strongest-implementation rule).
- Generated surface grows by one interface implementation and one command-text constant per
  `[SqlRecord]` under the Npgsql trigger; portable assemblies are byte-identical to today.
