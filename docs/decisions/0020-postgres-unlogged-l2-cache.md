# ADR-0020: PostgreSQL `UNLOGGED` table is the recommended L2 distributed cache

- Status: Accepted
- Date: 2026-06-30
- Related: [ADR-0004](0004-handler-result-caching.md) (handler result caching),
  [ADR-0017](0017-dependency-light-core.md) (provider defaults are opt-in packages),
  [the caching capability doc](../capabilities/caching.mdx) (usage).

## Context

`AddElarionHandlerCaching` ([ADR-0004](0004-handler-result-caching.md)) backs the `IHandlerCache` seam
with `HybridCache`, which is two-tier: an in-process L1 (`MemoryCache`) plus an optional L2
`IDistributedCache` that `HybridCache` auto-discovers from DI. Out of the box Elarion registers **no L2**,
so caching is L1-only: each instance keeps its own copy and a restart starts cold. Adding an L2 buys
cross-instance coherence, warm-restart survival, and stampede coordination.

The reflexive choice for an L2 is Redis. But Redis is a second datastore to provision, secure, back up,
monitor, and keep available — real operational weight for a team that already runs PostgreSQL for its
business data (as the rest of Elarion's provider story assumes: blobs, outbox, settings, resource grants
are all Postgres-backed). For the large middle of applications, the L1 already carries the hot path and
the L2 sees only modest, coherence-shaped traffic — a load Postgres serves comfortably. We wanted a
recommended L2 that adds **no new infrastructure**, while staying honest about when it is the wrong call.

PostgreSQL `UNLOGGED` tables fit a cache precisely. Data written to an `UNLOGGED` table skips the
write-ahead log, making writes cheaper and producing no WAL or replication traffic. The cost is that the
table is **truncated on a crash or unclean shutdown** and is **not present on physical standby replicas** —
both irrelevant for a cache, whose contents are always reconstructible from the source of truth. The worst
case is a cold repopulate, never data loss.

There are two viable `IDistributedCache`-over-Postgres packages: the community
`Community.Microsoft.Extensions.Caching.PostgreSql` (mature, but no first-class `UNLOGGED` knob and a
non-Microsoft dependency) and the **official `Microsoft.Extensions.Caching.Postgres`** (Microsoft +
azure-sdk, MIT). The official package exposes a first-class `UseWAL` option (`false` ⇒ `CREATE UNLOGGED
TABLE`, confirmed in its `SqlQueries`), is listed by Microsoft as implementing the alloc-free
`IBufferDistributedCache` path `HybridCache` prefers, and fits Elarion's "Microsoft + Npgsql only"
dependency policy.

## Decision

**Recommend a PostgreSQL `UNLOGGED` table as the default L2 distributed cache for most Elarion
applications, and ship a one-call convenience package that wires it.**

A new opt-in sibling package `Elarion.Caching.PostgreSql` — analogous to `Elarion.Blobs.PostgreSql` over
`Elarion.Blobs` — provides `AddElarionPostgreSqlHandlerCaching`:

- A connection-string overload (the recommended one-liner) and a full `Action<PostgresCacheOptions>`
  overload for complete control.
- Elarion defaults that make the cache table an auto-created `UNLOGGED` table: `UseWAL = false`,
  `CreateIfNotExists = true`, `SchemaName = "public"`, `TableName = "elarion_handler_cache"`. The caller's
  delegate runs **last**, so any default (including opting back into a WAL-logged, replicated table) is
  overridable.
- The method registers the official `Microsoft.Extensions.Caching.Postgres` distributed cache as the L2,
  then calls `AddElarionHandlerCaching()`; `HybridCache` auto-discovers the `IDistributedCache`.

The package depends only on `Elarion.Caching` and the official `Microsoft.Extensions.Caching.Postgres`
(which pulls `Npgsql`), keeping the heavy dependency opt-in per [ADR-0017](0017-dependency-light-core.md).
Like `Elarion.Blobs.PostgreSql`, it is **not** `IsAotCompatible` (it rides Npgsql).

The recommendation is deliberately balanced: the [caching capability doc](../capabilities/caching.mdx)
states plainly that Redis (or any other `IDistributedCache`) remains the better L2 when the cache itself
must be a very high-throughput, independently scaled, or multi-region store — and that any
`IDistributedCache` registered before `AddElarionHandlerCaching()` is used as the L2 just the same.

## Consequences

**Positive**

- The default path adds no new infrastructure: an app already on Postgres gets a shared, restart-surviving
  L2 by referencing one package and writing one line. The "reuse Postgres" story now spans blobs, outbox,
  settings, resource grants, and the cache.
- The `UNLOGGED`/`UseWAL = false` default makes cache writes cheap and replication-free without the
  caller knowing any Postgres internals; the tradeoff is documented and correct for cache data.
- Standardizing on the official Microsoft package keeps the dependency policy intact and gets the
  buffer-optimized `IBufferDistributedCache` path for free.

**Negative / accepted**

- One more opt-in package to maintain, and a dependency on a relatively new official package
  (`Microsoft.Extensions.Caching.Postgres`). The seam (`IHandlerCache`) and the in-process default
  (`Elarion.Caching`) are unaffected; an app that wants a different L2 ignores this package entirely.
- The cache table is auto-created by the package's own DDL (`CreateIfNotExists`), outside the app's EF
  migrations. This is intentional (the cache owns its table) but means the table is not described by the
  application's migration history.
- `UNLOGGED` data does not survive a crash and is absent on replicas. This is the right default for a
  cache, but a host that points the cache at a read replica, or that needs the cache to survive failover,
  must override `UseWAL`.

## Implementation

- `src/Elarion.Caching.PostgreSql/` — `PostgreSqlHandlerCacheServiceCollectionExtensions`
  (`AddElarionPostgreSqlHandlerCaching`); references `Elarion.Caching` and
  `Microsoft.Extensions.Caching.Postgres`.
- `Directory.Packages.props` pins `Microsoft.Extensions.Caching.Postgres`; the project is added to
  `Elarion.slnx`.
- `docs/capabilities/caching.mdx` gains an "Adding an L2 distributed cache" section leading with the
  Postgres-`UNLOGGED` recommendation and the Redis-when guidance.
- Tests in `tests/Elarion.Tests/Caching/` cover the registration defaults/overrides and a Docker-gated
  integration test that round-trips through the L2 and asserts the created table is `UNLOGGED`
  (`pg_class.relpersistence = 'u'`).
- The Billing sample backs its handler cache with `AddElarionPostgreSqlHandlerCaching` over the existing
  `billing` database, demonstrating the recommended path.
