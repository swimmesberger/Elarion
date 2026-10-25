# ADR-0091: A query declares its source entities; committed writes to them become re-query hints

- Status: Proposed
- Date: 2026-10-25
- Related: [ADR-0043](0043-client-events.md) (client events: after-commit, at-most-once re-query hints),
  [ADR-0090](0090-references-between-records-stay-in-the-database.md) (entity lifecycle events removed, partly for the
  EF Core surface they needed; section 4 says why this interceptor differs),
  [ADR-0085](0085-handler-declarations-are-projected-not-copied.md) (per-method declarations exported to the client), [ADR-0075](0075-ambient-tenant-scoping.md) (tenant scoping),
  [ADR-0007](0007-data-is-platform-module-as-plugin.md) (the shared data layer),
  [ADR-0025](0025-distributed-scheduler-coordination.md) (scale positioning).

## Context

A screen that shows a projection over several tables, such as a billing dashboard of open invoices, received payments
and overdue customers, has to re-query whenever one of those tables changes. Today an application builds this by hand
from client events (ADR-0043):

1. every command that writes one of the tables publishes an integration event;
2. a consumer projects each of those events onto an `IClientEvent` topic;
3. the browser subscribes to all of those topics and invalidates the query on each.

This has three problems.

- **The dependency is written at the wrong end.** "This dashboard reads payments" is a fact about the query, but it is
  written down at every writer. A writer added later that does not publish the event leaves the dashboard stale, and
  nothing reports it. Typical examples are an import job, a scheduled `ExecuteUpdate`, a command in another module and
  a database cascade.
- **The same list exists three times.** The writers' events, the projections and the browser's topic list must agree,
  and nothing checks that they do.
- **The events carry no intent.** They exist only to refresh a screen, yet each takes an outbox row and an inbox entry
  on the integration plane, which exists to deliver business facts reliably.

Generic row-change domain events (`EntitiesAdded<T>`, `EntitiesUpdated<T>`) would not help. A row change carries no
intent: "invoice updated" may be a payment, a correction or a column a background job stamped, so consumers would diff
columns to guess. Such events could not be complete while `ExecuteUpdate` is allowed, and they would run inline on the
most frequent writes. Elarion removed even the deletion variant (ADR-0090). One case needs no intent: a screen that
shows a projection over several tables only needs to know that a source changed, so that it can re-query. Client
events already have the right guarantees for that: after commit, at most once, a hint and never state. Two parts are
missing: a producer that derives the hints from the data layer, and a declaration on the query that says which hints
it depends on.

## Decision

Add opt-in **re-query hints**. A query handler declares the entity types it reads. After a transaction commits,
Elarion publishes a hint for each declared type the transaction wrote, and every query depending on that type is run
again. Three delivery modes decide who runs it and how often (section 6): each viewer, throttled (the default); each
viewer, immediately (for a few viewers); or the server, once for everyone who sees the same result, which it then
pushes (for many viewers).

### 1. The query declares its sources

```csharp
[Handler("billing.getDashboard")]
[RequeryOn<Invoice>, RequeryOn<Payment>, RequeryOn<Customer>]
public sealed class GetBillingDashboard(AppDbContext db)
    : IHandler<GetBillingDashboard.Query, Result<GetBillingDashboard.Dashboard>> {
    public sealed record Query : IQuery<Query, Dashboard>;
    // ...
}
```

- `RequeryOnAttribute<TEntity>` lives in `Elarion.Abstractions` and may be repeated. The generator accepts it only on a
  handler whose request is an `IQuery`. On any other handler, a stream handler included, it is a compile error.
- A source is an entity type, not a table name. A write to a type in an inheritance hierarchy counts for the type, its
  base types and its derived types. A write to an owned type counts for its owner.
- A query may name any entity of the shared data layer, another module's included (ADR-0007). Declaring a source is
  reading, which the query does anyway.
- Watched types are the union of the sources that the handlers of enabled modules declare. A disabled module's queries
  do not exist, so their sources are not watched.
- At startup every declared source must be mapped by a context that has the interceptor attached (section 4), or be
  registered as reported by hand. Otherwise startup fails and names the handler and the type, because a source that
  nothing reports would leave the screen stale without any error.
- `[Requery(RequeryMode.Immediate)]` or `[Requery(RequeryMode.Shared)]` on the handler chooses the delivery mode.
  Without it the mode is `Throttled`.

### 2. The contract exports the mode, not the sources

- The schema export writes `requery: "throttled" | "immediate" | "shared"` per method, next to `readOnly` (ADR-0085),
  only for methods that declare sources. The TypeScript generator carries it into `rpcMethodInfo`.
- The source types are not exported. Entity names belong to the internal model, which ADR-0043 keeps off the wire. The
  client does not need them either: a hint names the query method, and its wire name is already public.
- The generated events client gains `onStale(method, handler)` for the throttled and immediate modes, and
  `onResult(method, request, handler)` for the shared mode (section 6). Each is typed to the methods of its modes.
  Generated runtime code imports no UI library (AGENTS.md), so connecting `onStale` to TanStack Query is one line in
  the application:

  ```ts
  events.$client.onStale('billing.getDashboard', () =>
    queryClient.invalidateQueries({ queryKey: ['billing.getDashboard'] }))
  ```

  TanStack refetches only the queries a mounted component uses and marks the others stale.
- `onStale` fires on every hint for the method and on every `elarion.connected`, because a reconnect may have lost
  hints.
- Adding a watcher must not re-query the other watchers. Today a changed subscription set closes the `EventSource` and
  opens a new one, and the new connection's `elarion.connected` fires every handler. With watchers that come and go
  with screens, every navigation would re-query every watched query. The client therefore opens the new connection
  before it closes the old one. On the new connection's `elarion.connected` it calls `onStale` only for methods the old
  connection did not cover. After a dropped connection it calls all of them.
- The client keeps at most one `onStale` call in flight per method. The handler may return a promise, as
  `invalidateQueries` does (it settles when the refetch finishes). Hints that arrive while the call runs merge into one
  follow-up call after it settles, so queries never overlap and a refetch is never cancelled by the next hint. When the
  follow-up starts depends on the mode (section 6).
- While the document is hidden the client holds hints and calls `onStale` once when the page becomes visible again. A
  background tab does not re-query.
- The client delays each `onStale` call by a random jitter (by default up to 250 ms), so that the watchers of a tenant
  do not all re-query in the same instant.

### 3. Subscribing is authorized by the query method

- The browser subscribes to the framework topic `elarion.requery`, naming one method per subscription. A hint frame
  carries `{ "method": "billing.getDashboard" }`. In the shared mode the subscription also carries the query's request,
  and the frame carries the result (section 6).
- The resolver authorizes the subscription with the method's own declarations. The method must exist, which means its
  module is enabled. Its authorization requirements must pass the registered `IAuthorizer`, with
  `AuthorizationContext.HandlerType` set to the query handler so that global rules reading handler markers apply
  (ADR-0085). Its feature gate must be open for the caller. Any failure reads as not found, as for every topic. There
  is no second permission declaration.
- Resource requirements (`[RequireResource]`) need a request, so they are not evaluated. A type-level hint says nothing
  about a particular resource, and the re-query runs the full pipeline.
- A subscription needs an authenticated caller, as every topic does, even when the method is `[AllowAnonymous]`.
- Resolution stays all-or-nothing, as for other topics: one denied method fails the whole stream. The client should
  watch only methods that `canCall` allows.

### 4. Writes are collected per transaction

`AddElarionRequeryHints()` in `Elarion.ClientEvents` registers the feature and takes its options, including the types
reported by hand. `AddElarionRequeryHints<TDbContext>()` in the EF package attaches one interceptor to a context. The
interceptor collects the watched types a transaction writes into a scoped change set:

- **Tracked saves.** `Added`, `Modified` and `Deleted` entries of watched types.
- **Database cascades.** When a deleted principal's cascading or set-null foreign key reaches a watched type, that type
  is added too. This comes from the model and needs no query: finding the cascaded rows would need a query before the
  `DELETE`, but a type-level hint does not need the rows.
- **`ExecuteUpdate` and `ExecuteDelete`.** `IQueryExpressionInterceptor.QueryCompilationStarting` runs only while EF
  compiles a query. EF caches compiled queries, so the hook would see only the first execution of each query shape. A
  hook that refuses a query works there, because a query that throws is never cached; a hook that collects does not.
  So the interceptor tags the statement while it compiles (`TagWith`, which becomes a SQL comment in the cached SQL),
  and a command interceptor reads the tag on every execution. It recognizes the two methods by their `MethodInfo`, not
  by name. Any other command costs one prefix check.
- **Elarion's own bulk paths.** `ExecuteInsertAsync` reports its entity type.
- **Everything else, by hand.** Raw SQL (`ExecuteSql`), the AOT SQL tier and stored procedures report through
  `IRequeryHints.Changed<TEntity>()`, a scoped service in `Elarion.Abstractions` that writes to the same change set.
  Writes made by database triggers, by other applications on the same database and by migrations are not seen.

The change set follows the integration-event buffer's commit rules. It is published after an EF transaction commits or
after an autocommit save, and at the unit-of-work commit on the SQL tier. It is discarded on rollback and truncated on
a rollback to a savepoint. A report made by hand outside any transaction is published at once, because the write it
reports is already durable.

The interceptor reads tenants in `SavingChanges`, so it is registered after tenant scoping, which stamps the tenant of
inserted rows. The interceptor-order table on the EF Core page gains a row for it.

A save that writes no watched type costs one dictionary lookup per changed entry and publishes nothing.

ADR-0090 removed entity lifecycle events, partly because of the EF Core surface they needed. This interceptor avoids
the parts that made that surface fragile:

- **A wrong answer is cheap.** A missed hint leaves a screen stale until the next change or reconnect, and an extra
  hint costs one re-query. Lifecycle events had to be complete, because a missed event left orphaned rows. So nothing
  here fails closed, queries cascaded rows before a `DELETE`, or refuses `ExecuteDelete`.
- **No application code runs inside the save.** The interceptor only records types. Everything that reacts runs after
  commit, so there is no nested save and no recursion to guard against.
- **Public APIs only.** It uses the change tracker's entries, the model's foreign keys, and save, transaction and
  command interceptors that are attached per scope, as the integration-event buffer's are. The one singleton, the
  query-expression interceptor, is stateless: it adds a tag and reaches no scope.
- **The most fragile piece has a fallback.** If the spike (see "Before this is accepted") finds the `ExecuteUpdate` tag
  unreliable on a provider, those calls report by hand, like raw SQL. The analyzer (section 8) then flags an
  `ExecuteUpdate` or `ExecuteDelete` on a watched type that has no report next to it.

### 5. Hints are per entity type, partitioned by tenant

A hint is a pair: an entity type and a partition. The partition comes from the write:

| The write | Partition |
| --- | --- |
| A tracked entry of an `ITenantScoped<T>` entity | the row's tenant |
| `ExecuteUpdate`, `ExecuteDelete`, a bulk insert or a report by hand, on a tenant-scoped type | the ambient tenant; ADR-0075's filter confines `Execute*` to it |
| Any of these inside `SystemScope()` or with no tenant resolved | every partition |
| A type that is not tenant-scoped, or a host without tenant scoping | every partition |

`IRequeryHints.Changed<TEntity>(tenantId)` names the tenant explicitly.

The subscribing side derives its partition the way it derives the user scope. At subscribe time the resolver reads the
caller's tenant from `ITenantContext` and places the subscription in that tenant's partition and in the broadcast
partition. The client never names a tenant. A caller without a resolved tenant gets broadcast hints only. This
introduces a public `ClientEventScope.Tenant(id)` kind with the same derived subscription, so an application's own
topics can also publish to everyone in a tenant.

There is no per-user partition. The framework does not know which user a row belongs to, and `[ResourceFilter]` rules
are evaluated per query.

Row granularity is not part of this decision. A dashboard aggregates many rows, so a row key would not narrow its
hints. A detail screen keeps the resource-scoped client event it uses today, which also carries the id. Row hints would
need a mapping from request properties to entity keys, and `ExecuteUpdate` would still fall back to the whole type.
They can be added if a second application needs them.

### 6. Fan-out and delivery modes

All three modes share the publication path:

- After commit the change set becomes one publication: the transaction's deduplicated (type, partition) pairs. It
  crosses nodes through the existing `IClientEventBroadcaster` seam. Each node expands the types into the methods that
  watch them, using the catalog that every node builds the same way, and delivers to its own subscribers. The number of
  broadcasts therefore does not grow with the number of queries watching a type or with the number of watchers.
- Each node merges its pending publications and broadcasts at most once per 100 ms; the first goes out at once. If a
  publication does not fit one `NOTIFY` payload, its types collapse to the broadcast partition. Over-notifying is safe.
- Nothing runs without a change. No mode polls: a screen whose sources do not change costs nothing after its first
  query.

The modes differ in who runs the query and how often. Picking one is a question of how many people watch and whether
they all see the same result:

| Mode | Who runs the query | Fits |
| --- | --- | --- |
| `Throttled` (default) | each viewer, at most once per interval | any number of viewers, results per user |
| `Immediate` | each viewer, as soon as a hint arrives | a handful of concurrent viewers |
| `Shared` | each node, once for all its viewers of the same result | many viewers of a result that does not depend on the user |

**`Throttled`, the default.** Delivery is throttled per node and per (method, partition), latest wins:

- The first hint is delivered at once. Later hints within the interval (1 s by default) merge into one hint sent at the
  end of the interval. That trailing hint is always sent, so the last re-query runs after the last commit.
- **Backoff.** When the sources change faster than the interval, the interval grows. An interval that ends with merged
  hints doubles the next one, up to a cap (30 s by default). An interval that ends without a hint resets it to the
  base. Under a constant stream of writes a watched screen therefore refreshes at most once per cap interval but never
  stops updating, and one quiet interval after the stream ends it is back at the base interval. Every node receives
  every publication, so all nodes back off alike. The base interval and the cap are set in `AddElarionRequeryHints`.
- On the client, the follow-up of an `onStale` call waits at least as long as the last call took. A query that is slow
  to answer therefore spends at most half its time re-running, however fast its sources change.
- The server's backoff limits what a change rate costs all viewers together; the client's rule limits what a slow query
  costs one viewer. Together they cover both ways of falling behind: the data changes faster than the screens can
  usefully refresh, and a refresh takes longer than the interval.

**`Immediate`, for a few viewers.** There is no interval and no backoff: every hint is delivered as it arrives. The
client calls `onStale` at once and, when hints arrived during a call, starts the one follow-up as soon as the call
settles. Queries still never overlap. Under a burst of writes each viewer re-queries back to back, so the query's own
duration sets the rate, and after the last change a viewer is current within two query durations. The cost is that
during a burst every viewer keeps one query running. That suits an application with a handful of concurrent viewers,
such as an operations screen or a small team, and not a tenant with hundreds.

**`Shared`, for many viewers of the same result.** The server runs the query and pushes the result. Viewers do not
re-query.

- Each node keeps the latest result per (method, partition, request) that has a subscriber on that node. The first
  subscriber of a key triggers the first computation, and every new subscriber is greeted with the current result (the
  subscription-observer pattern of ADR-0043). When the last subscriber leaves, the key lingers for 5 s and is dropped.
- A node recomputes a key with the immediate loop: on a hint it starts at once unless a computation of that key is
  running. Hints that arrive during a computation mark the key dirty, and one recomputation follows as soon as the
  running one ends. At most one computation per key runs on a node, and the last published result always reflects the
  last commit.
- The result goes to the key's subscribers on the client-event stream with a version that grows per key, and the
  client drops a result older than the one it has. `onResult` hands it to the application, which stores it with
  `setQueryData`; the generated client validates it with the method's response schema. This is the versioned,
  latest-wins state tier that ADR-0043 already allows for quotes and presence.
- Each node computes for its own subscribers. A change therefore costs at most one computation per key on each node,
  ten for ten nodes, whatever the number of viewers. Results never cross nodes, so PostgreSQL's notification payload
  limit does not apply to them. Computing on a single node would save the other nine queries but needs every live
  connection routed to that node (ADR-0050). That is left as a later option.
- The computation runs the handler in the partition's tenant scope with an unauthenticated `ICurrentUser`, so a filter
  that depends on the user returns nothing instead of someone else's rows. Authorization happens once per viewer, when
  the viewer subscribes (section 3), not per computation. The generator rejects `Shared` on a handler that injects
  `ICurrentUser` or declares resource requirements.
- A node limits the number of shared keys one connection may hold, because every distinct request is a computation.

### 7. Scale: ten nodes on one PostgreSQL

- **`NOTIFY` rate.** At most one broadcast per node per 100 ms, so at most 100 per second for ten nodes, whatever the
  write rate. Each is a small autocommit statement on the broadcaster's own connection, outside the application's
  transactions. This matters because PostgreSQL holds a cluster-wide lock from the commit of a transaction that issued
  `NOTIFY` until the transaction ends. A `NOTIFY` inside every writing transaction would serialize the application's
  own writes.
- **Listeners.** Every node receives every publication, as with today's client events, and looks up each type in it.
- **Query load.** This is the real cost, and it is what the mode decides:
  - `Throttled`: each viewer re-queries a method at most once per interval, never while its previous re-query runs. If
    500 users watch a dashboard whose sources change constantly, that is up to 500 dashboard queries per second at
    first. The backoff reaches the 30 s cap within about 30 seconds, and from then on the same load is about 17 queries
    per second.
  - `Immediate`: up to one running query per viewer for as long as a burst lasts.
  - `Shared`: at most one running query per key on each node, so at most ten per key for ten nodes. The number of
    viewers changes only the number of frames sent.
- Past this tier the broadcaster seam is replaced, as ADR-0043 describes. The publication format stays the same.

### 8. Analyzer

A diagnostic analyzer warns when the body of a `[RequeryOn]` handler reads an entity type it does not declare: a
`DbSet<T>` or `Set<T>()` access, or a navigation to such a type. It is an analyzer and not part of the generator,
because the generator runs on every keystroke and must stay cheap. It does not follow calls into services, module APIs,
raw SQL or views, so the declaration stays the source of truth and the warning is only a reminder. It does not report
a declared source that the body never reads, because queries often read through a helper. If `ExecuteUpdate` falls
back to reporting by hand (section 4), the analyzer also flags an `ExecuteUpdate` or `ExecuteDelete` on a watched type
without a report in the same method.

### 9. Packages

- `Elarion.Abstractions`: `RequeryOnAttribute<TEntity>`, `RequeryAttribute` with `RequeryMode`, `IRequeryHints`,
  `ClientEventScope.Tenant`.
- `Elarion` and its generator: the method-to-sources catalog in the generated module registration, the `requery` mode
  in the handler client metadata, the checks on `Shared`, and the analyzer.
- `Elarion.ClientEvents`: the change set, the `elarion.requery` subscription, the expansion, the throttling and the
  shared-result computation.
- `Elarion.ClientEvents.EntityFrameworkCore` (new): the interceptor. It is a sibling package so that
  `Elarion.EntityFrameworkCore` does not depend on client events.
- The TypeScript generator: `rpcMethodInfo.requery`, `onStale` with its in-flight and hidden-page rules, `onResult`
  with its version guard, and the make-before-break reconnect.

## Consequences

- The reader states its dependency once. Writers added later, such as jobs, bulk paths, other modules and cascades,
  are covered without changes.
- Integration events and projections that exist only to refresh a screen go away. The integration plane keeps the
  events that carry intent.
- **Over-notification.** Any write to a source in the partition re-queries every watcher, including writes to columns
  the query does not read and to rows outside its filter. The interval and its backoff bound the cost.
- **Freshness gives way first under load.** In the throttled mode, while the sources change constantly, a watched
  screen refreshes only once per cap interval (30 s by default). Freshness degrades before the database does, which is
  the intended order. The other two modes keep freshness and pay for it: `Immediate` with a query per viewer, `Shared`
  with the restriction to results that do not depend on the user.
- **Shared results are authorized per subscription.** A viewer whose permission is revoked keeps receiving results
  until the stream reconnects, because a computation runs for everyone and not as the viewer. Hints do not have this
  gap, since every re-query runs the full pipeline.
- **Activity is visible within a tenant.** A watcher learns, at the resolution of the interval, that some row of a
  source changed in their tenant, including rows they cannot read. The hint carries no id and no content. An
  application for which the timing of changes is itself sensitive keeps explicit, resource-scoped client events.
- **Delivery is weaker than the hand-built path.** The hand-built path goes through the outbox, so it survives a crash
  of the producing node after commit. A hint is published from memory after commit, so a crash in that window loses it
  for watchers on other nodes. Watchers on the crashed node reconnect and re-query on `elarion.connected`; the others
  stay stale until the next change. An application that cannot accept this sets a `refetchInterval` as a safety net.
- **The raw-SQL gap.** Raw SQL, the AOT SQL tier and stored procedures are seen only when reported through
  `IRequeryHints`. Writes by triggers, other applications and migrations are not seen at all.
- **Read replicas.** A hint is sent after the commit on the primary. If re-queries are routed to a replica (ADR-0037),
  a lagging replica can return the old state. Such a host routes watched queries to the primary or uses a consistency
  token.
- Schemas gain a `requery` mode on watched methods, and the generated client gains `onStale` and `onResult`. Other
  methods stay
  byte-identical.

## Rejected alternatives

- **Polling a keyset query.** It stays the recommended choice when seconds of staleness are fine. As the mechanism here
  it costs one query per watcher per interval even when nothing changed, and the interval is the freshness.
- **Per-writer topics (today's way).** The dependency lives at the writers, so a forgotten event leaves the screen
  silently stale, and events that only refresh a screen burden the integration plane. They remain the right tool for a
  hint that carries an id or intent, such as a detail screen's.
- **Database triggers that call `pg_notify`.** They see every write, raw SQL included. But a `NOTIFY` in the writing
  transaction takes the cluster-wide lock at commit and serializes every watched write, and nothing can be throttled
  before that lock. Triggers would be schema objects that change whenever a query's sources change, so they would need
  generated migrations. They are PostgreSQL-only, although Elarion's migration and SQL tiers also support SQLite, and
  would have to read the tenant column in PL/pgSQL.
- **Logical replication.** It sees every committed change, including raw SQL, cascades and other applications. It needs
  `wal_level = logical` (a restart, and often restricted on managed databases), a replication slot that retains WAL
  while its consumer is down (a known way to fill a disk), one elected consumer, and a publication per table. That is
  change-data capture beyond this tier. An application that needs completeness can replace the producer with it and
  keep the publication format and the fan-out.
- **Exporting the source types and subscribing per type.** This exposes the internal model to the browser and would
  need an authorization vocabulary per entity type. The query method already has the right one.
- **Inferring the sources at run time** by recording which types a handler's queries touched. Sources vary by branch
  and are unknown until each branch has run. Elarion prefers declarations checked at build time.
- **Generic row-change domain events (`EntitiesAdded<T>`, `EntitiesUpdated<T>`).** They carry no intent, cannot be
  complete while `ExecuteUpdate` is allowed, and run inline on the most frequent writes (see "Context"). ADR-0090
  removed even the deletion variant.
- **Durable hints through the outbox.** An outbox row and a delivery for every write transaction, for a delivery that
  is at most once at the last hop anyway.
- **`Shared` as the default.** It works only for results that do not depend on the user, and it authorizes a viewer
  once per subscription instead of on every read. Both have to be chosen per method.
- **Sharing results through the handler cache (ADR-0004), evicted on each hint.** A computation that started before a
  commit and finishes after the eviction stores the old state as current, and every viewer reads it until the next
  change. The shared mode's dirty loop is that cache with the race closed: a hint during a computation always causes
  one more.
- **Computing a shared result on one node and broadcasting it through `NOTIFY`.** Results routinely exceed
  PostgreSQL's 8000-byte notification payload, and every broadcast would take the notification lock.
- **A fixed interval without backoff.** Under a constant stream of writes it re-queries every watcher at the full rate
  for as long as the stream lasts, and a query slower than the interval runs back to back.
- **Collecting `ExecuteUpdate` in `QueryCompilationStarting` alone.** It runs once per query shape, so every later
  execution would be missed (section 4).
- **Other names.** `[InvalidateOn]` reads like the server-side `[CacheInvalidate]`. `[LiveQuery]` suggests that the
  server pushes results, as live queries do in other frameworks, while Elarion pushes only a hint. `[RequeryOn]` says
  what the client does.

## Before this is accepted

- Confirm with Npgsql and SQLite that a tag added in `QueryCompilationStarting` reaches the SQL of `ExecuteUpdate` and
  `ExecuteDelete`, and that the command interceptor sees it on every execution.
- Measure the defaults (100 ms broadcast window, 1 s base interval, 30 s backoff cap, 250 ms jitter) with a benchmark
  in `tests/Elarion.Benchmarks`.
