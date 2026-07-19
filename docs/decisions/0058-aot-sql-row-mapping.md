# ADR-0058: AOT-native SQL row mapping — explicit generated mappers, not call-site interception

- Status: Accepted
- Date: 2026-07-13
- Related: [ADR-0057](0057-postgresql-sql-migration-runner.md) (the EF-free/AOT tier this completes),
  [ADR-0006](0006-incremental-source-generator-conventions.md) (generator conventions),
  [ADR-0023](0023-canonical-json-serialization.md) (JSON columns ride the canonical accessor),
  [ADR-0051](0051-postgresql-bulk-insert.md) (precedent: raw-parity benchmark gate).

## Context

ADR-0057 gives the EF-free/AOT tier its schema half (startup SQL migrations). The access half is
still hand-rolled: every EF-free host writes its own `DbDataReader` → record mapping and parameter
binding, per row type, by hand.

**Why not Dapper.AOT.** It is actively maintained and its *emitted code* is excellent (ordinal-cached
readers, no boxing). But its consumption model is call-site interception of the reflection-based
Dapper API, and the resulting gaps are architectural, not maturity issues:

- **Silent fallback on indirection.** Only direct inline Dapper calls are intercepted; a call the
  interceptor cannot statically see (your own helper method wrapping `Query<T>`, a generic utility)
  compiles cleanly and silently executes classic reflection Dapper — which under NativeAOT is a
  *runtime* failure. The one guarantee an AOT-first framework must give ("if it builds, it maps")
  is exactly the one interception cannot make.
- `QueryMultiple`/`GridReader` only partially supported; private types unsupported; interceptors
  C#-only. All documented as of Dapper.AOT 1.0.52 (2026).

**Prior art: MiniOrmAot** (the author's 2023 prototype, to be archived when this ships) validated
the alternative consumption model — an *explicit generated mapper contract* (`[GenerateMapper]` →
`IDataRecordMapper<T>` in DI). There is no reflection twin to fall back to: an unmapped type is a
missing type, i.e. a compile error, and helper-method indirection is free because the mapper is a
value you pass around, not a call-site pattern to be recognized. The implementation, however,
predates the incremental-generator era: `ISyntaxContextReceiver` + Scriban templating in the
analyzer, name-based per-column lookups with per-column `await` per row, boxing through
`TypedValue(object?)`, and a `new T()` requirement that rules out `required`-member records. The
shape won; the implementation is superseded.

**Prior art: jOOQ.** The Java benchmark for SQL-first data access — "the full power of SQL" instead
of an ORM hiding it. Three of its ideas matter here, with different verdicts under C#/Roslyn:

- *Schema-derived code generation* (jOOQ's core: the database schema, not the entity class, is the
  source of truth for generated metamodel + records). Adoptable — but not directly: Roslyn
  generators are hermetic (no DB connections at compile time), so it needs the two-stage chain
  Elarion already uses for `rpc-schema.json` (design-time tool → committed artifact → generator).
- *Plain SQL templating with typed, injection-safe parameters* (jOOQ's `resultQuery("... {0}")`
  tier). Adoptable, and C# has a *better* native mechanism than Java: interpolated string handlers
  bind at compile time.
- *The typesafe query-builder DSL* itself. Not adoptable at sane cost: it is decades of grammar
  surface (predicates, functions, window clauses, CTEs, dialect emulation), and a C# rebuild
  converges on reimplementing LINQ-to-SQL — the exact translation layer this tier exists to avoid.
  The property worth keeping ("full power of SQL") is delivered more directly by real SQL plus
  typed identifiers than by a DSL that must chase the PostgreSQL grammar release by release.

## Decision

Ship **`Elarion.Sql`**: AOT-native SQL row mapping and parameter binding as explicit generated
contracts — the MiniOrmAot consumption model rebuilt to Elarion's generator and performance
conventions. `IsAotCompatible`; dependencies are BCL + `Microsoft.Extensions.*` abstractions only
(no Dapper, no hard Npgsql reference); the generator is bundled (single home, like `Elarion` core).

### Consumption model (the deliberate inversion of Dapper.AOT)

- `[SqlRecord]` on a row type (optional table name; `[SqlColumn("…")]`/`[SqlIgnore]` per property;
  snake_case naming default, matching the EF convention) → a generated sealed
  `{Type}SqlMapper : ISqlRowMapper<T>` exposing:
  - `Read(DbDataReader)` / `ReadAllAsync(DbDataReader, ct)` — ordinal map computed once per result
    set, then synchronous typed `GetFieldValue<T>` per row; no name lookups, no boxing, no
    per-column `await`;
  - `BindParameters(DbCommand, T)` — typed, generated per property;
  - generated `TableName` / column-name constants, so hand-written SQL composes without stringly
    duplication (`$"SELECT {OrderSqlMapper.Columns.All} FROM {OrderSqlMapper.TableName}"`);
  - generated **statement constants for the mechanical, clause-free statements only** — `Insert`
    (full-row `INSERT INTO t (…) VALUES (@…)`), `Select` (the SELECT-list prefix), and
    `Columns.AllAssignments` (the `UPDATE … SET` list). These contain zero query logic — they are the
    column enumeration a human would type mechanically — and being `const string`s they compose at
    compile time (`OrderSqlMapper.Insert + " ON CONFLICT DO NOTHING"`). Anything with a predicate,
    join, or clause stays hand-written; that is the line between removing boilerplate and building a
    query DSL.
- Mappers are stateless: a static `Instance` for DI-free minimal hosts, plus a generated
  registration extension for seam-style injection. Nominal records with `required`/`init` are
  first-class (object-initializer emission — no parameterless-constructor requirement).
- **No interception, ever.** If a type isn't `[SqlRecord]`, there is no mapper to call — failure is
  a compile error at the call site, never a silent reflection fallback. This is the property to
  document and defend; features that would reintroduce a runtime-discovery path are rejected.

### Full-power-of-SQL surface (the jOOQ lessons, translated)

- **Safe SQL interpolation (v1).** A `Sql($"UPDATE orders SET status = {status} WHERE id = {id}")`
  entry point backed by an interpolated string handler: interpolated values become `DbParameter`s
  at compile time (collections expand to parameter lists for `IN`), literal text passes through
  untouched. Full SQL stays full SQL — window functions, CTEs, `ON CONFLICT`, any PostgreSQL
  feature — with injection safety and zero runtime parsing. Like the mapper, it is an explicit
  type at the call site, not interception; fragments compose (a reusable `WHERE` piece is just a
  value). This is the C#-native equivalent of jOOQ's plain-SQL templating tier.
- **Schema-derived metamodel and drift verification (committed; sequenced after v1).** jOOQ's
  source-of-truth inversion, adapted to hermetic generators: a design-time tool starts a throwaway
  Testcontainers PostgreSQL, brings it to schema (ADR-0057 migration scripts — or any other means,
  see below), introspects the catalogs, and writes a committed `elarion-sql-schema.json`; the
  bundled generator consumes it as an `AdditionalFile` to (a) emit the table/column metamodel and
  (b) **validate every `[SqlRecord]` against the real schema** — missing column, type mismatch,
  nullability drift = `ELSQL` build diagnostic. The migrations thereby become the compile-checked
  source of truth for the whole tier (schema half and access half verify each other). v1 ships
  record-first (`[SqlRecord]` standalone, no snapshot required); the tool is a committed
  deliverable of the package, not an option — same chain shape as `rpc-schema.json`.

  *Rejected alternative: parsing the migration scripts inside the generator* (jOOQ's `DDLDatabase`
  shape — the scripts as `AdditionalFiles` driving emission directly). Superficially attractive
  (no tool, no container), but it means reimplementing the PostgreSQL grammar in the analyzer:
  migrations are arbitrary SQL (`ALTER` chains, `DO` blocks, extension-defined types, generated
  columns, views), and the schema is the *fold* of every script — a parser must replay full ALTER
  semantics, and every unparseable statement either fails the build or silently corrupts the
  model. jOOQ ships a complete SQL parser to make this work and still documents its gaps. The
  running PostgreSQL is the only complete parser of PostgreSQL — introspection understands
  whatever SQL was written because the database executed it. It also *decouples* the packages
  rather than coupling them: the snapshot tool's input is "a database brought to schema by any
  means" — the ADR-0057 runner, EF migrations, or a hand-managed DB — so `[SqlRecord]` drift
  verification works even for EF-tier apps with raw-SQL edges, and neither package needs the
  other. Per-keystroke cost seals it: one cached JSON `AdditionalFile` versus re-parsing N SQL
  files on every edit (ADR-0006).

### Implementation bar

- Generator follows ADR-0006 in full: `ForAttributeWithMetadataName`, equatable models, diagnostics
  as data, cache-reuse tests. Diagnostics prefix `ELSQL`.
- JSON columns: `[SqlJson]` serializes through the canonical accessor's `JsonTypeInfo<T>`
  (ADR-0023) — AOT-strict, one JSON config everywhere.
- Provider-specific parameter typing (e.g. `NpgsqlDbType.Jsonb`) via provider-aware emission behind
  the assembly trigger (precedent: the keyset emitter under Npgsql) — no separate provider package
  until a second provider demands one.
- **Benchmark gate** in `tests/Elarion.Benchmarks` (the ADR-0051 discipline): generated read path at
  parity with a hand-written ADO.NET reader in time and allocations, with a Dapper.AOT column for
  the docs comparison.

### Non-goals (what keeps this from becoming an ORM)

No change tracking, no LINQ or query translation, no relationship/graph mapping, no query
generation — SQL stays hand-written; the generated constants (columns and the clause-free `Insert`/
`Select`/`AllAssignments` statements) remove the boilerplate, not the SQL: nothing generated ever
contains a predicate.
**No query-builder DSL**, explicitly including a jOOQ-style one: rejected above as
LINQ-to-SQL-by-another-name; requests for it get the safe-interpolation + metamodel answer.
The EF tier is untouched: EF Core remains the default data access for every host that can use it;
`Elarion.Sql` is the AOT/no-EF tier's companion, chosen by tier, not preference. MiniOrmAot's
statement generator, versioned-entity concurrency, and tenant-column features are deliberately not
carried over in v1 — each returns only on demonstrated consumer need, as its own decision.

## Consequences

- The AOT tier is now a coherent pair: ADR-0057 (schema) + this (access), documented together in
  the deployment/AOT concept doc with the tier decision table.
- Docs carry an honest "why not Dapper.AOT" section: architectural difference (explicit contract
  vs call-site interception), what that buys (build-time mapping guarantee, indirection freedom),
  and what Dapper.AOT does better where true — respectful, benchmark-backed, not marketing.
- [MiniOrmAot](https://github.com/swimmesberger/MiniOrmAot) gets archived with a pointer here as
  the validated predecessor.
- `Elarion.Migrations.PostgreSql` (ADR-0057) stays independent of this package — its single
  history-row reader remains hand-written; neither package requires the other.

## Addendum (2026-07): call-site DX rework

The v1 surface shipped, then a design panel (five independent reviews + a synthesis) found the
architecture sound but the call site leaking machinery. The following DX rework landed as four slices;
none touched the hard constraints (read-path allocation parity — re-verified 1.00× time and
allocations vs hand-written ADO.NET at 1k and 100k rows — no reflection, no interception, no runtime
codegen, AOT-clean, SQL stays hand-written).

- **Self-mapping.** The generator emits a partial making each `[SqlRecord]` type implement
  `ISqlRecord<T>` (static-abstract `SqlMapper` resolving the cached singleton, plus `InsertCommandText`
  and typed `Table`/`Select` fragments). The query extensions resolve the mapper from the type
  argument — `db.QueryAsync<Order>($"…")`, no mapper threaded through the call. C# cannot locate a
  *separate* mapper class from the row type (no associated types), so the static abstract lives on the
  row itself; the row must be `partial` (`ELSQL010`) and must not shadow the generated member names
  (`ELSQL011`). Resolution is a static field read, devirtualized under AOT — zero measurable cost.
- **`:raw` removed.** The generated `Table`/`Select` fragments splice with no annotation (forgetting
  the marker is now impossible), and the `AppendFormatted` format overload — with its runtime
  `FormatException` for a typo'd spec — is gone; a stray `{x:fmt}` is now a compile error. Dynamic
  identifiers use `SqlStatement.Verbatim(col)`, the one greppable trusted-text door.
- **`SqlWhere`.** The no-DSL answer to the dominant call-site shape (a list with optional filters):
  accumulate parenthesized predicate fragments carrying their own parameters, render `WHERE (a) AND (b)`
  or nothing. It kills `WHERE 1=1`, keeps the obvious thing to type safe, and the same accumulator
  drives a page query and its `count(*)`. It knows only the `WHERE`/`AND` joiners — no predicate is
  generated, staying on the boilerplate side of the DSL line.
- **Write + streaming tier.** `InsertAsync`/`InsertManyAsync` (the latter owns the one-transaction
  reused-prepared-command loop; `sqlSuffix` appends `ON CONFLICT …`), `QuerySingleOrDefaultAsync`
  (throws on more than one row), `QueryUnbufferedAsync` (`IAsyncEnumerable` streaming), and a
  transaction-taking `ExecuteAsync` — on both `DbDataSource` (pooled connection per call) and
  `DbConnection`. `InsertManyAsync` is a *convenience* batch, not bulk COPY — ADR-0051 stays the bulk
  path.
- **Constructor + `Verbatim`.** `new SqlStatement($"…")` replaces the `SqlStatement.Of` factory; `Raw`
  became `Verbatim`; `Empty` and `operator+` compose fragments.

*Rejected from the panel:* renaming the type to `Sql` (it shadows the `Elarion.Sql` namespace for
`Elarion.*`-rooted consumers — CA1724; the type stays `SqlStatement`); a `Sql<T>.Where/.OrderBy/.Limit`
statement-builder (clause-named members are a query-DSL seed — `SqlWhere` accumulation plus hand-written
SQL covers the optional-filter bucket instead); and per-type `Entity.ById`-style static helpers (ORM
pressure). Self-mapping JSON rows read the canonical JSON accessor from a process-global ambient
(`ElarionSqlJson`) — the one unavoidable global, since a static-abstract mapper cannot take a
constructor dependency; it is installed at startup by a hosted service the generated
`AddElarionSqlMappers` registers only for JSON-column assemblies (justifying a
`Microsoft.Extensions.Hosting.Abstractions` dependency), and read lazily at first JSON use so there is
no static-init ordering fragility.

### Considered future addition: source-generated binary COPY for the AOT tier

The EF tier's high-throughput bulk-insert path (ADR-0051 — `DbSet<T>.ExecuteInsertAsync` over Npgsql
binary `COPY`) is unavailable to this tier on two counts: its entry point and column metadata come from
the EF model, and its per-column writers are compiled at runtime via `System.Linq.Expressions`, so the
package is **not** `IsAotCompatible`. The EF-free/AOT tier therefore has no binary-COPY path today —
`InsertManyAsync` (a reused-prepared-command loop, one round trip per row) is the ceiling.

A **source-generated** COPY would close that gap without borrowing the EF path: the `[SqlRecord]`
generator already emits typed per-column parameter binding (`BindParameters`) and knows each column's
`NpgsqlDbType` under the `[UseElarionSql(Provider = SqlProvider.Npgsql)]` trigger, so emitting a
`WriteRowTo(NpgsqlBinaryImporter, T)` per column is the same compile-time, reflection-free shape —
AOT-clean, "if it builds, it maps." Exposed as a `connection.CopyAsync(rows)` extension (or a generated
mapper method), it would give the AOT tier raw-`NpgsqlBinaryImporter` COPY speed. This mirrors the
migration split (EF migrations for the EF tier, `Elarion.Migrations.PostgreSql` for the AOT tier): two
implementations by tier, because the mechanism genuinely differs (runtime-compiled vs source-generated),
never a repackaging of the EF path. It ships behind a benchmark gate at raw-COPY parity (the ADR-0051
discipline) and dogfoods in the EdgeTelemetry ingest handler. Deferred to its own decision — v1 keeps
`InsertManyAsync` as the batch path and points high-throughput bulk load at the EF tier.

## Addendum (2026-07): scoped session and the EF-free unit of work

The v1 access surface hung the query/write extensions on `DbDataSource` (a pooled connection per call)
and `DbConnection`. That left a correctness gap: the framework `TransactionDecorator` runs on the
provider-neutral `IUnitOfWork` seam, which this tier never implemented, so an EF-free host fell back to
the core no-op unit of work — a command that wrote more than once was **not** rolled back on failure. And
the `DbDataSource` overloads were structurally incapable of joining a transaction (a fresh connection per
call), so pointing a handler at them silently opted out of atomicity.

The fix introduces the tier's missing scoped state — `ISqlSession`, one connection pinned for a request
scope plus the transaction currently open on it (the SQL analogue of EF's shared scoped `DbContext`) —
and `SqlUnitOfWork` over it, implementing `IUnitOfWork` with the same semantics as
`EfUnitOfWork<TDbContext>`: real transaction on the shared connection, nested handlers join via a
savepoint (PostgreSQL forbids a second physical transaction on one connection), and best-effort
`SET LOCAL lock_timeout` on Npgsql (detected structurally, since the package keeps no Npgsql dependency).
There is no change tracker to flush — the handler's statements have already run on the connection inside
the transaction — so commit only commits. `AddElarionSqlSession()` registers the session alone (per-call
auto-commit); `AddElarionSqlUnitOfWork()` layers the transactional unit of work on it. Both live in
`Elarion.Sql` — no new package, because `IUnitOfWork` is already in `Elarion.Abstractions`.

Which data source a session opens from is the `IElarionSqlDataSourceProvider` seam — the **single source of
truth**, and the only thing the tier resolves. There is deliberately no hard `GetRequiredService<DbDataSource>()`
inside the session and no hidden default that reaches for an ambient `DbDataSource`: the provider is registered
explicitly, in two steps like the EF tier (`AddDbContext` then `AddElarionUnitOfWork<TDbContext>`). Register it
with `AddElarionSqlDataSource(sp => …)` (build and own the source here, the container disposes it),
`AddElarionSqlDataSource()` (wrap a `DbDataSource` already in the container, e.g. from `AddNpgsqlDataSource`),
or `AddElarionSqlDataSourceProvider<T>()` (a scoped provider that routes per request — a tenant's database or a
read replica). This is consistent with the migration runner, which also takes an explicit data source rather
than resolving a global one, and the seam is designed for that strongest (per-scope routing) implementation.

Preferring API design over pre-1.0 compatibility, the `DbDataSource` receiver was **removed**, not kept
alongside: the query/write surface now lives on `ISqlSession` (the handler entry point) and the
`DbConnection` primitive it delegates to (DI-free / NativeAOT hosts, tooling, tests that own their
connection). Two receivers, not three, and the one a handler injects is transaction-aware by
construction — the silent non-transactional path is gone rather than merely discouraged. The EdgeTelemetry
sample migrated its handlers from `NpgsqlDataSource` to `ISqlSession` (call sites unchanged); its e2e test
covers ingest-then-query through the migrated schema.
