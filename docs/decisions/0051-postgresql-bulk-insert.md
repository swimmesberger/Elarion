# ADR-0051: Bulk insert as an EF-shaped seam with a PostgreSQL binary COPY provider

- Status: Accepted
- Date: 2026-07-11
- Related: [ADR-0038](0038-client-assigned-entity-identity.md) (client-assigned Guid keys — why bulk
  insert needs no generated-key round trip),
  [ADR-0041](0041-blob-streaming-connections-clone-the-context-connection.md) (the one-connection-channel
  principle the provider follows), [ADR-0025](0025-distributed-scheduler-coordination.md) (scale
  positioning).

## Context

EF Core has no bulk insert. `SaveChanges` sends change-tracked batched INSERTs — at 10k+ rows that is
roughly an order of magnitude slower than PostgreSQL's native ingestion path (binary `COPY … FROM
STDIN`), and the change tracker allocates per entity on top. The EF team has a long-standing design
sketch for exactly this feature ([dotnet/efcore#27333](https://github.com/dotnet/efcore/issues/27333)
"bulk import", plus the small-batch sibling
[dotnet/efcore#29897](https://github.com/dotnet/efcore/issues/29897) `ExecuteInsert`), both parked in
the backlog with agreed shape points: hangs off the set, non-tracking, streaming input
(`IAsyncEnumerable<T>`), database-generated values deliberately **not** fetched back, a provider hook
for the native mechanism.

The library landscape is unattractive as a dependency: `EFCore.BulkExtensions` went revenue-gated
dual-license in 2023; its MIT fork and `linq2db` carry a second LINQ engine's worth of surface;
`PhenX.EntityFrameworkCore.BulkInsert` is closest in spirit but external. Meanwhile applications built
on Elarion (imports, seeding, event backfills, outbox replays) keep re-writing hand-rolled
`NpgsqlBinaryImporter` loops that duplicate what the EF model already knows: table and column names,
value converters, which columns the store generates.

Elarion-specific tailwinds: ADR-0038 makes entity keys client-assigned v7 Guids, so the classic bulk
pain point — getting generated identities back — mostly does not exist here; and the framework already
targets "the one PostgreSQL the app runs" (ADR-0025), so a Postgres-first provider covers the shipped
tier.

## Decision

Two packages, seam/provider split, aligned with the dotnet/efcore#27333 / #29897 shape so a future EF
Core native API is a rename rather than a redesign:

- **`Elarion.EntityFrameworkCore.BulkOperations`** (neutral, depends only on
  `Microsoft.EntityFrameworkCore.Relational`): `ExecuteInsertAsync` extensions on `DbSet<TEntity>`
  (both `IEnumerable<T>` and streaming `IAsyncEnumerable<T>`, optional `BulkInsertOptions` bag,
  returns the row count), the `IBulkInsertProvider` seam, and `BulkInsertTargetResolver` — the shared
  EF-metadata walk that decides the insertable column list identically for every provider.
- **`Elarion.BulkOperations.PostgreSql`**: the binary COPY provider, registered EF-natively as an
  options extension — `options.UseNpgsql(…).UseElarionPostgreSqlBulkOperations()` ships the provider
  into the EF internal service provider exactly like EF provider plugins do. No app-DI registration,
  no new configuration channel.

Name: `ExecuteInsertAsync` joins EF's `ExecuteUpdateAsync`/`ExecuteDeleteAsync` family — the existing
"set-based, non-tracking, bypasses the change tracker" vocabulary — and matches the #29897 proposal.

Semantics (all mirroring the EF sketch):

- **Non-tracking.** Entities are read, never attached; nothing is written back to them.
- **Store-generated columns are omitted from the COPY column list** (identity/serial — `GENERATED
  ALWAYS` even rejects explicit values — computed columns, on-add store defaults) and their values are
  not fetched back; client-side value generators do not run (they need tracked entries), so
  caller-assigned keys are required — which is Elarion's convention anyway (ADR-0038).
- **The context's own connection, the caller's transaction.** The COPY runs on
  `Database.GetDbConnection()` (opened through EF's bookkeeping), so an open
  `Database.CurrentTransaction` contains it: rollback discards the bulk rows together with the
  handler's other writes. No second connection channel (the ADR-0041 principle). COPY is
  all-or-nothing — any error aborts the whole stream, never partial rows.
- **Value converters are honored** by inlining `ConvertToProviderExpression` into a compiled
  per-column writer, cached per (model, entity type): the hot loop does typed
  `NpgsqlBinaryImporter.WriteAsync<T>` calls addressed by the mapping's `NpgsqlDbType` (store-type
  name as fallback for name-addressed mappings), no per-value boxing or reflection.
- **Complex properties (value objects) flatten** — nested chains compile into the per-column writers
  with short-circuiting null propagation (a null anywhere along the chain nulls the column), entirely
  at plan-build time: an entity without complex properties produces a byte-identical plan and hot loop.
- **Opt-in upsert** via the options bag: `OnConflict = DoNothing | Update` stages the COPY into a
  per-call temporary table and merges with `INSERT … SELECT … ON CONFLICT` (COPY cannot express
  conflict handling). Conflict target defaults to the primary key (`Update`) / any unique constraint
  (`DoNothing`); `ConflictProperties` selects an alternate target, validated against the model's
  declared keys and unique indexes before the database is touched. The default `Throw` path is the
  untouched direct COPY — upsert's extra hop is paid only by callers who ask for it.
- **Unsupported shapes fail loud before touching the database**: TPT/entity splitting, owned types,
  JSON-mapped complex collections, non-discriminator shadow properties, and derived instances in a
  base set (TPH itself works — the discriminator constant is written per set). Each error names the
  offending member and the way out.

Benchmarks (`tests/Elarion.Benchmarks`, `PostgreSqlBulkInsertBenchmarks`, real PostgreSQL via
Testcontainers) gate the claim the same way ADR-0042's benchmarks gate the actor runtime: Elarion's
`ExecuteInsertAsync` must sit at raw-`NpgsqlBinaryImporter` parity (the ceiling) and be compared
against `SaveChanges` (the floor), `EFCore.BulkExtensions.MIT`, `linq2db`, and `PhenX`.

Measured at acceptance (2026-07-11, Apple M-series, postgres:17-alpine in a local container, 8-column
row; mean of 10 single-op iterations):

| Method | 1k rows | 10k rows | 100k rows | Allocated @100k |
|---|---|---|---|---|
| `SaveChanges` (baseline) | 43.9 ms | 218.9 ms | 1,563.9 ms | 1,003.8 MB |
| **Elarion `ExecuteInsertAsync`** | **5.3 ms** | **37.7 ms** | **124.4 ms** | **4.6 MB** |
| EFCore.BulkExtensions.MIT | 7.5 ms | 51.7 ms | 124.4 ms | 49.7 MB |
| linq2db `BulkCopy` | 5.8 ms | 40.6 ms | 191.0 ms | 20.7 MB |
| PhenX `ExecuteBulkInsertAsync` | 5.9 ms | 38.4 ms | 115.8 ms | 20.7 MB |
| Raw `NpgsqlBinaryImporter` | 6.0 ms | 35.3 ms | 125.5 ms | 4.6 MB |

Time and allocations both land at raw-COPY parity (allocations within 2 KB of hand-written at every
row count — the compiled writers add nothing per value); ~12× `SaveChanges` at 100k rows.

## Alternatives considered

- **Depend on an existing library.** `EFCore.BulkExtensions` is revenue-gated dual-license — not
  acceptable as a framework dependency; the MIT fork and `linq2db` import large foreign surfaces for
  the one verb Elarion needs; `PhenX` is MIT and good, but a framework whose EF packages otherwise own
  their store access should not outsource its narrowest, most stable hot path. All remain benchmark
  competitors instead.
- **`DbContext`-level extension (`context.BulkInsertAsync(...)`)** — the shape every library uses.
  Rejected: the set-scoped `ExecuteInsertAsync` matches EF's own `Execute*` family and the #29897
  sketch, and pins the entity type the same way `ExecuteUpdate` does.
- **App-DI registration (`AddElarionBulkOperations<TDbContext>()`).** Rejected: the provider is
  per-context-options, not per-scope — an options extension is how EF composes provider plugins, keeps
  the feature usable on contexts constructed without a service provider (tools, tests), and leaves no
  ordering trap between two registration channels.
- **Multi-row `INSERT` fallback provider in the neutral package** (the #27333 "works everywhere"
  default). Deferred, not rejected: a missing provider fails loud with the registration hint. Postgres
  is the shipped tier (ADR-0025); a SQL Server `SqlBulkCopy` provider is the anticipated second
  implementation of the same seam and resolver.
- **Generated-value return (`RETURNING`).** Out of scope: it needs the temp-table shape *plus* a
  result-mapping story, which the EF team also treats as a separate feature (#29897's
  `ExecuteInsertReturning`, #27320) — and client-assigned v7 Guid keys (ADR-0038) remove most of the
  demand. Upsert, which shares the temp-table mechanics, *did* make the cut as the opt-in
  `OnConflict` behaviors because it is the feature applications actually switch bulk libraries for.
- **Running client-side value generators for unset keys.** Rejected: generators hang off tracked
  entries; silently generating for some value shapes and not others is a trap. Callers assign keys
  (v7 Guids per ADR-0038).

## Consequences

- Applications get a one-liner native-feeling bulk path
  (`await context.Orders.ExecuteInsertAsync(orders, cancellationToken: ct)`) that composes with the
  unit-of-work transaction the handler already runs in, at raw-COPY throughput.
- The insert column list is decided by shared, provider-neutral resolver rules; a future SQL Server
  provider only supplies the mechanism (`SqlBulkCopy`) and its store-generated refinement.
- If EF Core ships #27333/#29897 natively, migration is mechanical: same call site position, same
  semantics; Elarion's extension can be retired or forwarded (pre-1.0 clean-break policy applies).
- The provider package references `Npgsql.EntityFrameworkCore.PostgreSQL` and reads
  `NpgsqlTypeMapping.NpgsqlDbType` from an `Internal` namespace (version-coupled, pinned centrally);
  the store-type-name fallback keeps exotic and plugin mappings working if that cast ever stops
  matching.
- Not AOT-flagged (EF packages are exempt per repo convention); the compiled-expression writers fall
  back to the expression interpreter under NativeAOT like EF itself does.
