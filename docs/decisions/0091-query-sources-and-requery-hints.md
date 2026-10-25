# ADR-0091: A query declares its source entities; committed writes to them become re-query hints

- Status: Proposed
- Date: 2026-10-25
- Related: [ADR-0043](0043-client-events.md) (client events: after-commit, at-most-once re-query hints),
  [ADR-0090](0090-references-between-records-stay-in-the-database.md) (entity lifecycle events removed, partly for the
  EF Core surface they needed; section 4 says why this interceptor differs),
  [ADR-0085](0085-handler-declarations-are-projected-not-copied.md) (per-method declarations exported to the client),
  [ADR-0075](0075-ambient-tenant-scoping.md) (tenant scoping),
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

  TanStack refetches only the queries a mounted component uses and marks the others stale. The example keeps
  `invalidateQueries`' default `cancelRefetch: true` on purpose. A fetch that started before the commit, such as a
  focus refetch, must be cancelled, because its success would clear the stale flag and swallow the hint. Watched
  queries should also get a high `staleTime` (`Infinity`), so that TanStack's own focus and mount refetches do not run
  next to the hints.
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
- The client spreads its re-queries. Each connection picks a fixed fraction between 0 and 1 when it opens, and delays
  every `onStale` call by that fraction of the spread window that the hint carries (section 6). The calls that
  `elarion.connected` triggers use the same fraction, over the last window the client saw for the method and at least
  1 s, because a listener reconnect fires every subscriber on a node at once. A fixed fraction keeps the order stable,
  so load patterns can be reproduced.

### 3. Subscribing is authorized by the query method

- The browser subscribes to the framework topic `elarion.requery`, naming one method per subscription. A hint frame
  carries the method and the spread window in milliseconds (section 6), for example
  `{ "method": "billing.getDashboard", "spread": 1250 }`. In the shared mode the subscription also carries the
  query's request, and the frame carries the result (section 6).
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

`IRequeryHints.Changed<TEntity>(tenantId)` names the tenant explicitly. A system job that knows which tenants it touched
should name them, because a broadcast makes every tenant re-query.

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
- Each node merges its pending publications and broadcasts at most once per 100 ms; the first goes out at once. A
  publication that does not fit one `NOTIFY` payload is split across several. Only a type whose partition list alone
  exceeds a payload, such as a system job touching hundreds of tenants, collapses to the broadcast partition.
  Over-notifying is safe, but a collapse makes every tenant re-query, so splitting comes first.
- Nothing runs without a change. No mode polls: a screen whose sources do not change costs nothing after its first
  query.

The modes differ in who runs the query and how often. Picking one is a question of how many people watch and whether
they all see the same result:

| Mode | Who runs the query | Fits |
| --- | --- | --- |
| `Throttled` (default) | each viewer, at most once per interval | any number of viewers, results per user |
| `Immediate` | each viewer, as soon as a hint arrives | a handful of concurrent viewers |
| `Shared` | each node, once for all its viewers of the same result | many viewers of a result that does not depend on the user |

The research note [live-dashboards-and-fan-out.md](research/live-dashboards-and-fan-out.md) surveys how other systems
solve this, from their primary sources. Live-query systems that serve many viewers share one execution between viewers
of the same query in the same authorization context. Hasura merges identical subscriptions into one SQL statement and
refetches every second. Meteor runs one observer per identical query per process. Convex runs a query once for
concurrent callers and caches the result. PostGraphile re-runs per subscriber with a 500 ms throttle, warns about the
thundering herd, and recommends that only for small user bases. `Immediate` and `Throttled` are the per-viewer model;
`Shared` is the shared one.

**`Throttled`, the default.** Delivery is throttled per node and per (method, partition), latest wins:

- The first hint is delivered at once. Later hints within the interval merge into one hint sent at the end of the
  interval. That trailing hint is always sent, so the last re-query runs after the last commit.
- **The interval grows with the audience, not with the change rate.** A node gives each key a budget of re-queries per
  second (20 by default). With W viewers of the key on the node, the interval is `max(1 s, W / budget)`, and each hint
  carries that interval as its spread window. Each viewer delays its re-query by its own fixed fraction of the window
  (section 2), so the audience re-queries evenly across the window instead of all at once. A key therefore costs a node
  at most its budget in re-queries per second, however many people watch and however fast the sources change. A small
  audience keeps the 1 s interval and a spread of a fraction of a second. The window counts every subscriber the hint
  reaches, so a hint to the broadcast partition is spread wider.
- **Backoff follows load.** On the server the interval grows with the audience. On the client the follow-up of an
  `onStale` call waits at least as long as the last call took, so when the database slows down, re-queries space
  themselves out: a query that is slow to answer spends at most half its time re-running. Together they cover both
  ways of falling behind: more viewers than the database should serve at once, and a refresh that takes longer than
  the interval. An earlier draft also backed off on the change rate. It was dropped, because it made two viewers of a
  busy tenant wait 30 s, and no system in the research note lowers freshness because writes are frequent.
- When a key's interval exceeds 5 s, the node logs once that the method is a candidate for `Shared`. The budget and
  the base interval are set in `AddElarionRequeryHints`.

**`Immediate`, for a few viewers.** There is no interval, budget or spread: every hint is delivered as it arrives. The
client calls `onStale` at once and, when hints arrived during a call, starts the one follow-up as soon as the call
settles. Queries still never overlap. Under a burst of writes each viewer re-queries back to back, so the query's own
duration sets the rate, and after the last change a viewer is current within two query durations. The cost is that
during a burst every viewer keeps one query running. That suits an application with a handful of concurrent viewers,
such as an operations screen or a small team, and not a tenant with hundreds. The node logs once when a key has more
than 20 viewers on it.

**`Shared`, for many viewers of the same result.** The server runs the query and pushes the result. Viewers do not
re-query.

- Each node keeps the latest result per (method, partition, request) that has a subscriber on that node. The first
  subscriber of a key triggers the first computation, and every new subscriber is greeted with the current result (the
  subscription-observer pattern of ADR-0043). When the last subscriber leaves, the key lingers for 5 s and is dropped.
- A node recomputes a key with the immediate loop. Every hint for the key raises its generation. A computation reads
  the generation before it opens its snapshot, and starts at once unless one is already running. If the generation
  moved while a computation ran, one more follows as soon as it ends. At most one computation per key runs on a node,
  and the last published result always reflects the last commit. (Convex checks the same condition before it reuses a
  result; Meteor's polling driver uses the same follow-up rule.)
- The result goes to the key's subscribers on the client-event stream, with its generation as the version, and the
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
- A shared subscription lives at most 10 minutes. The node then ends it, and the client subscribes again through the
  normal path (section 3), opening the new subscription before it drops the old one. A revoked permission therefore
  takes effect within that bound. Other systems leave this open: Supabase re-checks broadcast authorization only when
  the token changes, and SignalR groups have no revocation at all.
- A node limits the number of shared keys one connection may hold, because every distinct request is a computation.

### 7. Scale: ten nodes on one PostgreSQL

- **`NOTIFY` rate.** At most one broadcast per node per 100 ms, so at most 100 per second for ten nodes, whatever the
  write rate. Each is a small autocommit statement on the broadcaster's own connection, outside the application's
  transactions. This matters because PostgreSQL holds a cluster-wide lock from the commit of a transaction that issued
  `NOTIFY` until the transaction ends. A `NOTIFY` inside every writing transaction would serialize the application's
  own writes.
- **Listeners.** Every node receives every publication, as with today's client events, and looks up each type in it.
  The `LISTEN` connection never opens a transaction: a session that holds a transaction open stops PostgreSQL from
  cleaning the notification queue, and when the queue is full, commits that issued `NOTIFY` fail. Each node reports
  `pg_notification_queue_usage()` as a metric.
- **Query load.** This is the real cost, and it is what the mode decides:
  - `Throttled`: at most the budget per key on each node, 20 re-queries per second by default and spread evenly, so
    at most 200 per key for ten nodes. If 500 users on one node watch a dashboard whose sources change constantly,
    each refreshes every 25 s; spread over ten nodes, every 2.5 s. The node's log points such audiences at `Shared`.
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
  the query does not read and to rows outside its filter. The budget bounds the cost.
- **Freshness gives way first under load.** In the throttled mode a large audience on one node waits longer between
  refreshes: 25 s for 500 viewers on one node at the default budget. A small audience keeps 1 s however busy its
  tenant is. Freshness degrades before the database does, which is the intended order. The other two modes keep
  freshness and pay for it: `Immediate` with a query per viewer, `Shared` with the restriction to results that do not
  depend on the user.
- **Shared results are authorized per subscription.** A viewer whose permission is revoked keeps receiving results
  until the subscription expires (after 10 minutes at most) or the stream reconnects, because a computation runs for
  everyone and not as the viewer. Hints do not have this gap, since every re-query runs the full pipeline.
- **Panels can disagree.** Under Read Committed each statement reads its own snapshot, so a watched handler that runs
  several statements can mix two commits when one lands between them. Such a handler should read in one read-only
  `REPEATABLE READ` transaction; the docs will say so. This is not new, but re-queries that follow writes closely make
  it visible.
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
  change. Reading HybridCache's source suggests that a run overlapping a tag invalidation is stored as valid (not yet
  confirmed by a test), and its stampede protection is per instance. The shared mode's generation is that cache with
  the race closed: a hint during a computation always causes one more.
- **Serving the shared result to re-queries instead of pushing it.** Viewers would keep getting hints and re-query
  through the full pipeline, and the node would answer from the shared result. Authorization would stay per request,
  with no revocation gap, and nothing new would cross the wire. But every change would still cost one request per
  viewer and an extra round trip, and removing those is what the shared mode is for. The subscription lifetime bounds
  the revocation gap instead.
- **Computing a shared result on one node and broadcasting it through `NOTIFY`.** Results routinely exceed
  PostgreSQL's 8000-byte notification payload, and every broadcast would take the notification lock.
- **A fixed interval.** Its cost grows with the audience: under a constant stream of writes it re-queries every viewer
  at the full rate for as long as the stream lasts, and a query slower than the interval runs back to back.
- **Backoff driven by the change rate** (an earlier draft: double the interval while hints keep coming, up to 30 s).
  It makes a small audience pay for a busy tenant, it has no precedent in the systems the research note covers, and
  the audience budget bounds the same cost without it.
- **Collecting `ExecuteUpdate` in `QueryCompilationStarting` alone.** It runs once per query shape, so every later
  execution would be missed (section 4).
- **Other names.** `[InvalidateOn]` reads like the server-side `[CacheInvalidate]`. `[LiveQuery]` suggests that the
  server pushes results, as live queries do in other frameworks, while Elarion pushes only a hint. `[RequeryOn]` says
  what the client does.

## Before this is accepted

- Confirm with Npgsql and SQLite that a tag added in `QueryCompilationStarting` reaches the SQL of `ExecuteUpdate` and
  `ExecuteDelete`, and that the command interceptor sees it on every execution.
- Measure the defaults with a benchmark in `tests/Elarion.Benchmarks`: the 100 ms broadcast window, the 1 s base
  interval, the budget of 20 re-queries per second per key and node, the log thresholds (a 5 s interval, 20
  immediate viewers) and the 10-minute shared subscription lifetime.
- Decide whether the client that made a write may skip the hint for it, as Turbo and Laravel allow. This works only
  when a merged publication comes entirely from that client's writes.
