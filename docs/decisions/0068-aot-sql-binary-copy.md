# ADR-0068: Source-generated binary COPY and staged upsert for the AOT SQL tier

- Status: Accepted
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
    ConflictColumns = ["id"],                                               // column names; required for Update (no key metadata on this tier)
}, ct);
```

- The name mirrors the EF tier's `ExecuteInsertAsync` deliberately: same verb, same non-tracking
  set-based semantics, same conflict vocabulary (`Throw`/`DoNothing`/`Update`), so moving a workload
  between tiers is a receiver change, not a redesign. The options bag is tier-local
  (`SqlBulkInsertOptions`) because its members speak SQL (column names), not EF model properties.
- Input shapes: `IEnumerable<T>` and `IAsyncEnumerable<T>` (rows stream into COPY as produced), plus
  an `IReadOnlyList<T>` fast path that iterates by index — a swept, reused `List<T>` of struct rows
  flushes without a boxed enumerator.
- `OnConflict = Throw` (default) streams straight into the target table — fastest, all-or-nothing on
  any error. `DoNothing`/`Update` stage through a per-call `TEMP` table cloning only the mapped
  columns' definitions (`CREATE TEMP TABLE … AS SELECT {columns} FROM {target} WITH NO DATA`, dropped
  best-effort in a `finally`), COPY into it, then merge with one
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

## Implementation notes (what shipped, where it deviates from the sketch above)

- **Conflict vocabulary matches the EF tier exactly**: `SqlBulkInsertConflictBehavior.Throw` (default)
  / `DoNothing` / `Update` — the sketch's `None` was renamed to `Throw` because ADR-0051 already
  shipped that name and cross-tier vocabulary was the point.
- **Interface shape**: `CopyCommandText` + `CopyTableName` + `CopyColumnList` (staging needs table and
  column list separately; generic code cannot reach the mapper's consts) +
  `WriteRowAsync(NpgsqlBinaryImporter, TSelf row, CancellationToken)`. The row passes by value, not
  `in` — C# forbids `in` parameters on async methods, and a struct copy per row is noise against a
  network write. The static member delegates to an instance method on the generated mapper, which owns
  the lazy `JsonTypeInfo` fields for `[SqlJson]` columns.
- **No generation-time `NpgsqlDbType` table.** Column writes use `importer.WriteAsync(value)` CLR-type
  inference — the exact semantics of the generated `BindParameters` — with an explicit `NpgsqlDbType`
  only where the parameter path is explicit (`Jsonb`). This guarantees COPY/INSERT value parity by
  construction and eliminates the anticipated unmappable-column diagnostic: any type that passes
  ELSQL001 is COPY-writable. ELSQL011 instead gained the four reserved copy member names, collected
  provider-independently in the transform and reported only when COPY emission is on.
- **`Update` requires explicit `ConflictColumns`** — this tier has no key metadata to infer a conflict
  target from, and PostgreSQL requires one for `DO UPDATE`. `DoNothing` may omit it (skips on any
  unique constraint). Conflict columns validate against the mapped column list before the database is
  touched; unique-constraint coverage is the database's check.
- **Connection semantics** follow the tier's other session helpers (Dapper-style): a closed connection
  is opened for the call and closed afterwards; an open one — the scoped session's — is left open, and
  everything runs inside `ISqlSession.CurrentTransaction`.
- **Benchmarks** (`--filter "*SqlCopy*"`, real PostgreSQL 17 via Testcontainers; monitoring strategy,
  10 iterations, 2026-07-20 on an M-series laptop): the generated path sits at raw parity, and the
  gate holds.

  | Rows | Raw importer | Generated `ExecuteInsertAsync` | `InsertManyAsync` |
  | --- | --- | --- | --- |
  | 1k | 6.9 ms | 6.1 ms (0.97×) | 192 ms (30×) |
  | 10k | 37.3 ms | 42.5 ms (1.14×) | 1 817 ms (49×) |
  | 100k | 138.6 ms | 121.5 ms (0.89×) | 19 200 ms (140×) |

  Upsert-shaped (staged merge into a fully populated table, conflict target `id`), against a
  hand-written staged loop: 14.4 ms vs 13.4 ms at 1k (1.07×), 92.0 ms vs 81.6 ms at 10k (1.13×),
  379.0 ms vs 381.4 ms at 50k (0.99×) — and 15–26× faster than the prepared `ON CONFLICT`
  `InsertManyAsync` alternative. Managed allocations are at raw parity (~1.0×) on every COPY path;
  `InsertManyAsync` allocates ~60× more (289 MB vs 4.6 MB at 100k rows). The staging overhead is a
  near-constant ~2× of the direct COPY at these sizes — cheap enough that the docs recommend the
  upsert path for every sweep-flush size.

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
