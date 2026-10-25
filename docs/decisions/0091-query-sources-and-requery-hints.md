# ADR-0091: A query declares its source entities; committed writes to them become re-query hints

- Status: Proposed
- Date: 2026-10-25
- Related: [ADR-0043](0043-client-events.md) (client events: after-commit, at-most-once re-query hints),
  [ADR-0090](0090-references-between-records-stay-in-the-database.md) (entity lifecycle events removed, partly for the
  EF Core surface they needed; section 2 says why this interceptor differs),
  [ADR-0085](0085-handler-declarations-are-projected-not-copied.md) (per-method declarations exported to the client;
  amended by section 5), [ADR-0075](0075-ambient-tenant-scoping.md) (tenant scoping),
  [ADR-0080](0080-errors-are-a-declared-contract-with-a-stable-code.md) and
  [ADR-0086](0086-one-answer-for-unexpected-errors.md) (error frames in section 7),
  [ADR-0007](0007-data-is-platform-module-as-plugin.md) (the shared data layer),
  [ADR-0025](0025-distributed-scheduler-coordination.md) (scale positioning),
  [ADR-0004](0004-handler-result-caching.md), [ADR-0037](0037-read-replica-routing-and-consistency-tokens.md),
  [ADR-0049](0049-role-leases.md), [ADR-0050](0050-role-holder-proxy.md) and
  [ADR-0053](0053-bidirectional-client-connections.md) (cited where they
  bound this decision), and two research notes, both from primary sources:
  [live-dashboards-and-fan-out.md](research/live-dashboards-and-fan-out.md) (how other systems deliver and fan out
  live updates) and [change-detection-for-live-queries.md](research/change-detection-for-live-queries.md) (where other
  frameworks detect that data changed).

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
most frequent writes. Elarion removed even the deletion variant (ADR-0090).

One case needs no intent: a screen that shows a projection over several tables only needs to know that a source
changed, so that it can re-query. Client events already have the right guarantees for that: after commit, at most
once, a hint and never state. Two parts are missing: a producer that derives the hints from the data layer, and a
declaration on the query that says which hints it depends on.

The research note found that live-query systems serving many viewers share one execution between viewers of the same
query in the same authorization context: Hasura merges identical subscriptions into one SQL statement, Meteor runs one
observer per identical query, and Convex caches results. PostGraphile re-runs per subscriber, warns about the
thundering herd, and recommends that only for small user bases. No system in the survey lowers freshness because the
change rate rises; they bound cost by sharing or by a throttle. This decision follows the same split.

The second research note asked where frameworks notice a write. Rails (`after_commit`, under Turbo's broadcasts),
Laravel, Django, Hibernate and ABP detect tracked writes in ORM hooks, and every one of them documents that set-based
statements (`update_all`, `QuerySet.update()`, bulk HQL) bypass those hooks; none intercepts them. Elixir chose the
other end on purpose: Ecto removed model callbacks in 2.0, Phoenix contexts broadcast explicitly, and Ash declares
notifiers on actions and sends them after commit. Only the database itself, through triggers or logical replication,
sees every writer. This decision detects tracked writes automatically, has set-based and raw writes reported at the
call site with the build enforcing it, and leaves the database-level producers as replacements.

## Decision

Add opt-in **re-query hints**. A query handler declares the entity types it reads. After a transaction commits,
Elarion publishes which of those types the transaction wrote, and every query depending on them is run again. Two
delivery modes decide who runs it:

| Mode | Who runs the query | Fits |
| --- | --- | --- |
| `PerViewer` (default) | each viewer, at once for a small audience and throttled for a large one | any audience, results that differ per user |
| `Shared` | each node, once for all its viewers of the same result, which it then pushes | large audiences of a result that does not depend on the user |

Nothing polls. A screen whose sources do not change costs nothing after its first query.

### 1. Declaring sources

```csharp
[Handler("billing.getDashboard")]
[RequeryOn<Invoice>, RequeryOn<Payment>, RequeryOn<Customer>]
public sealed class GetBillingDashboard(AppDbContext db)
    : IHandler<GetBillingDashboard.Query, Result<GetBillingDashboard.Dashboard>> {
    public sealed record Query : IQuery<Query, Dashboard>;
    // ...
}
```

- `RequeryOnAttribute<TSource>` lives in `Elarion.Abstractions` and may be repeated. The generator accepts it only on a
  handler whose request is an `IQuery`; on any other handler, a stream handler included, it is a compile error.
- **A source is a key type.** On EF it is the mapped entity type. On the EF-free SQL tier, whose rows map to projection
  records, it is an application-declared marker class per table (`sealed class InvoicesTable`) that readers and
  writers name alike. Two projection records over one table would otherwise be two sources, and a writer reporting one
  would miss the readers of the other.
- **What counts as a write to a source.** A write to a type in an inheritance hierarchy counts for the type, its base
  types and its derived types. A write to an owned type counts for its owner. A write to a many-to-many join entity
  counts for the join type and both ends.
- A query may name any entity of the shared data layer, another module's included (ADR-0007). Declaring a source is
  reading, which the query does anyway.
- **The mode.** `[Requery(RequeryMode.Shared)]` on the handler selects the shared mode; without it the mode is
  `PerViewer`. `[Requery(Partitions = RequeryPartitions.All)]` declares a cross-tenant screen (section 4).
- **Declarations every host reads alike.** Whether a type is published, and whether its hints go to every tenant, is
  declared where every host sees the same answer: in the EF model, or on the SQL tier's marker class.
  - EF: `ExcludeFromRequery()`, `IncludeInRequery()` and `RequeryBroadcast()` in entity configuration. They live in
    `Elarion.EntityFrameworkCore` as model annotations, as tenant scoping's annotation does, so neither a data layer
    nor a framework package needs a client-events reference to use them.
  - SQL tier: `[RequerySource(Broadcast = …)]` on the marker class, in `Elarion.Abstractions`. A SQL-tier copy record
    names the source it writes with `[WritesRequerySource<InvoicesTable>]`. A hand report publishes whether or not
    the writing host registered the source; `o.Source<T>()` (below) serves only the declaring host's startup check.
  - Framework-owned types (outbox, inbox, idempotency, role leases, scheduler, settings, audit, leased-work rows) are
    excluded by default. `IncludeInRequery()` opts one back in.
- **Startup checks.** Each fails startup and names the handler and the type:
  - a declared source that no attached context maps and that is not registered as reported by hand
    (`AddElarionRequeryHints(o => o.Source<T>())`);
  - a declared source that is excluded from publication;
  - a keyless, view, `ToSqlQuery` or function-mapped source, which nothing ever writes, unless it is registered as
    reported by hand. The message names both remedies: declare the base entities, or register the type, which is how a
    materialized view refreshed by a job becomes a source;
  - watched methods in the generated catalog while the feature is not registered, or registered with no producer.

### 2. Producing publications

`AddElarionRequeryHints()` in `Elarion.ClientEvents` registers the feature and its options. The EF sibling package
attaches the producer to a context with `AddElarionRequeryHints<TDbContext>()`.

- **Every writing host registers the producer, and it publishes every type it writes** that a context maps and that is
  not excluded. Only the receiving node filters, through the catalog it already uses (section 3). Whether a write
  counts as watched therefore never depends on which modules the writing host enables: a worker host that serves no
  dashboard still publishes its nightly job's writes. Because framework types are excluded, an idle cluster sends no
  `NOTIFY`.
- **What the producer collects** into a scoped change set:
  - tracked `Added`, `Modified` and `Deleted` entries;
  - database cascades: when a deleted principal's cascading or set-null foreign key reaches another type, that type is
    added too. This comes from the model and needs no query. A report (below) expands the same way, because it does
    not say whether it stands for a delete; over-notifying is safe;
  - Elarion's bulk paths: `ExecuteInsertAsync` reports its entity type, and the SQL tier's
    `session.ExecuteInsertAsync` reports when its copy record names a source;
  - everything else through reports at the call site, `IRequeryHints.Changed<TSource>()` (a scoped service in
    `Elarion.Abstractions`): `ExecuteUpdate` and `ExecuteDelete`, raw SQL, the SQL tier and stored procedures. Writes
    made by database triggers, by other applications on the same database and by migrations are not seen; the
    replacement producers below cover them.
- **Set-based statements are reported, not intercepted.** `ExecuteUpdate` and `ExecuteDelete` bypass the change
  tracker, as set-based statements bypass the hooks of every ORM the second research note covers, and none of those
  frameworks intercepts them. An EF hook would have to run where EF compiles a query (`QueryCompilationStarting`),
  which happens once per query shape because compiled queries are cached, and EF's precompiled queries never run it.
  Instead, the call site reports the write:

  ```csharp
  await db.Invoices.Where(i => i.DueDate < today).ExecuteUpdateAsync(s => s.SetProperty(i => i.Overdue, true), ct);
  hints.Changed<Invoice>();
  ```

  An analyzer makes this a build error rather than a stale screen (section 10). It reads the written type from the
  generic argument of `ExecuteUpdate<T>`/`ExecuteDelete<T>`, so it works with precompiled queries too.
- **Commit tracking.** The change set keeps a transaction depth that three things maintain: the EF transaction
  interceptor (transactions EF begins, and those attached with `UseTransaction`); new internal after-commit,
  after-rollback and savepoint hooks on `SqlUnitOfWork`; and an ambient `System.Transactions` transaction, through
  `TransactionCompleted`, as the settings notifier already does. The change set is published after the outermost
  commit, discarded on rollback, and truncated on a rollback to a savepoint. A report at depth 0 publishes at once,
  because the write it reports is already durable; an autocommit `ExecuteUpdate` and its report are that case. The SQL
  tier also opens transactions outside the unit of work, through `ISqlOwnedSession.BeginTransactionAsync` and
  `ISqlDatabase.BeginTransactionAsync`, which return an `ISqlTransaction`. A report for a write inside one uses
  `IRequeryHints.Changed<TSource>(transaction)`, which publishes after that transaction commits, through internal hooks
  on its implementation.
- **Reports in untracked transactions warn.** A report at depth 0 checks whether it can see an open transaction that
  the change set does not track: a `DbContext` resolved in the scope whose `Database.CurrentTransaction` is set, which
  happens when that context has no producer attached. If so, it still publishes at once and logs a warning naming the
  source and the context, as Ash warns about notifications from a transaction it did not start. A transaction opened
  with raw ADO.NET on a bare connection is invisible; the docs require a report inside it to come after the commit.
- **Missing producers.** `IRequeryHints` defaults to a no-op that core registers with `TryAdd`. It logs a warning once
  per process and source when it drops a report, so a CLI, the migration runner or a forgotten worker shows up in its
  own logs. Where the feature is registered, startup logs once for every registered `DbContext` without the producer
  attached. A host that registers neither cannot be detected.
- **Ordering.** The producer reads tenants in `SavingChanges`, so it is registered after tenant scoping, which stamps
  the tenant of inserted rows. The interceptor-order table on the EF Core page gains a row for it.
- A save that writes no published type costs one lookup per changed entry and publishes nothing.

ADR-0090 removed entity lifecycle events, partly because of the EF Core surface they needed. This producer avoids the
parts that made that surface fragile:

- **A wrong answer is cheap.** A missed hint leaves a screen stale until the next change or reconnect, and an extra
  hint costs one re-query. Lifecycle events had to be complete, because a missed event left orphaned rows. So nothing
  here fails closed at run time, queries cascaded rows before a `DELETE`, or refuses `ExecuteDelete`.
- **No application code runs inside the save.** The producer only records types. Everything that reacts runs after
  commit, so there is no nested save and no recursion to guard against.
- **Public APIs only.** It uses the change tracker's entries, the model's foreign keys, and the save and transaction
  interceptors attached per scope, as the integration-event buffer's are. There is no query-expression interceptor, no
  command interceptor and no SQL-comment scanning, and nothing to re-verify on an EF Core upgrade.

**Replacement producers.** The publication (section 3) is a public contract, and `IRequeryPublications` (in
`Elarion.ClientEvents`) lets another producer feed it, alongside or instead of the EF one. Two are documented as
recipes, not shipped:

- **Statement-level triggers into a change table.** An `AFTER … FOR EACH STATEMENT` trigger on each published table
  inserts its (table, tenant) pairs from the transition tables into a change table, and a role holder (ADR-0049) polls
  it, gap-safely, and publishes. It sees every database write, including raw SQL, database-only cascades, other
  applications and data migrations, and rows appear only on commit, so no commit tracking is needed. The costs are
  schema objects the migration plan has to keep in step with the model, tenant logic in SQL, an extra insert per write
  statement, cleanup of the table, and no SQLite path. A trigger that calls `pg_notify` directly is not offered: it
  takes the notification lock at commit and serializes every watched write (section 8).
- **Logical replication.** A role holder reads `pgoutput` from one slot and maps relations to sources. It sees
  everything committed, but it needs `wal_level = logical`, a slot that retains WAL while its reader is down, and
  database permissions that managed services often restrict, which puts it past this tier.

A team with other writers on the database, or that wants no reports at all, adopts one of them; the delivery side
does not change. A team that replaces the EF producer this way leaves the unreported-write analyzer (section 10)
switched off.

### 3. Publication and fan-out

- **One publication per transaction.** After commit the change set becomes one publication: the transaction's
  deduplicated (type, partition) pairs. It crosses nodes through the existing `IClientEventBroadcaster` seam as an
  envelope on a reserved topic, `elarion.requery.publication`, which is neither in the catalog nor subscribable. Its
  payload carries a format version `v` and the pairs; the type id is the EF entity type name, which also covers
  shared-type entities, or the marker class's full name. A decorator over `IClientEventLocalDelivery` intercepts it on
  every node and expands the types into the methods that watch them, using the catalog every node builds the same way.
  Any broadcaster works unchanged, including a replacement past this tier. The number of broadcasts does not grow with
  the number of queries watching a type or with the number of viewers.
- **Batching.** Each node merges its pending publications and sends at most one broadcast round per 100 ms; the first
  goes out at once. A round is one `NOTIFY` unless its merged payload does not fit, in which case it is split across
  several. Only a type whose partition list alone exceeds a payload, such as a system job touching hundreds of tenants,
  collapses to the broadcast partition.
- **Failure and shutdown.** When `pg_notify` fails (no pooled connection, a failover, a full queue), the node keeps
  merging newer changes into the pending set, which stays bounded because it is deduplicated, and retries with backoff
  from 100 ms to 5 s, logging and counting each failure. On graceful shutdown it flushes the pending set with a 2 s
  timeout.
- **Rolling deploys.** Old and new nodes share the channel. A receiver skips an unknown topic, scope kind or payload
  version at debug level instead of logging a malformed notification. The loss is limited to the rollout, which
  at-most-once delivery allows.
- **The listener.** The `LISTEN` connection never opens a transaction: a session that holds one open stops PostgreSQL
  from cleaning the notification queue, and when the queue is full, commits that issued `NOTIFY` fail. Each node
  reports `pg_notification_queue_usage()` as a metric.

### 4. Partitions

A hint is a pair of a source and a partition. The partition comes from the write:

| The write | Partition |
| --- | --- |
| A tracked entry of an `ITenantScoped<T>` entity | the row's tenant |
| `ExecuteUpdate`, `ExecuteDelete`, a bulk insert or a hand report, on a tenant-scoped type | the ambient tenant; ADR-0075's filter confines `Execute*` to it |
| Any of these inside `SystemScope()` or with no tenant resolved | every partition |
| A type that is not tenant-scoped, or a host without tenant scoping | every partition |

- `IRequeryHints.Changed<TSource>(tenantId)` names the tenant explicitly. A system job that knows which tenants it
  touched should name them, because a broadcast makes every tenant re-query.
- In a host with tenant scoping, a watched type that is not tenant-scoped logs a startup warning unless the model
  declares `RequeryBroadcast()`. Every write to such a type reaches every tenant, and its timing is visible across
  tenants.
- **Subscribers.** The subscribing side derives its partition the way it derives the user scope: the resolver reads the
  caller's tenant from `ITenantContext` at subscribe time and places the subscription in that tenant's partition and
  the broadcast partition. The client never names a tenant. In a host with tenant scoping, a method with any
  tenant-scoped source requires a resolved tenant; without one the subscription is dropped (section 5). The docs say
  the tenant must be derivable from what `EventSource` sends: a cookie, a claim, the host or the query string.
- **Cross-tenant screens.** A handler declaring `[Requery(Partitions = RequeryPartitions.All)]` reads across tenants
  (it enters `SystemScope()` itself). Its subscribers need no tenant, still pass the method's authorization, and
  receive every partition's hints. The docs warn that such a screen re-queries on every tenant's writes. Combining it
  with `Shared` is a compile error.
- This introduces a public `ClientEventScope.Tenant(id)` kind with the same derived subscription, so an application's
  own topics can also publish to everyone in a tenant.
- There is no per-user partition. The framework does not know which user a row belongs to.

Row granularity is not part of this decision. A dashboard aggregates many rows, so a row key would not narrow its
hints, and `ExecuteUpdate` would still fall back to the whole type. A detail screen keeps the resource-scoped client
event it uses today, which also carries the id.

### 5. Subscribing and the contract

- **The topic.** The browser subscribes to the framework topic `elarion.requery`, naming one method per subscription,
  and for `Shared` also the query's request.
- **One by one.** Unlike ordinary topics, which stay all-or-nothing, requery subscriptions resolve one at a time. An
  unknown, denied or invalid method is dropped silently and behaves like a method whose sources never change, so a
  closed feature flag or a revoked permission cannot kill the tab's other client events. Each drop is logged once per
  (method, reason) at debug level. A denial cannot be told apart from quiet, so nothing leaks about the method, and the
  RPC re-query still returns the real answer. A dropped `Shared` subscription is different: it gets a fallback frame
  (section 7).
- **Authorization.** The method's own declarations authorize the subscription. The method must exist (its module
  enabled), its requirements must pass the registered `IAuthorizer`, and its feature gate must be open for the caller.
  `AuthorizationContext.HandlerType` is set to the query handler, so global rules that read handler markers apply, and a
  new `AuthorizationContext.IsSubscription` (true on topic and requery subscriptions alike) tells a rule that there is
  no request. This amends ADR-0085's statement that `HandlerType` is null on the subscription path; that ADR is edited
  in the PR that adds `IsSubscription` (section 12, step 4). Resource requirements need a request and are not evaluated,
  since a type-level hint says nothing about a particular resource. A subscription needs an authenticated caller, as
  every topic does, even when the method is `[AllowAnonymous]`.
- **Limits.** All configurable: 16 `Shared` keys per connection and 1000 per node; requery subscriptions on more than
  16 connections per user per node are dropped, while other topics are unaffected; W (section 6) counts at most 4
  subscriptions per user and key. A subscription beyond a count cap is dropped and logged. A `Shared` result over
  256 KB is not pushed; its subscribers get a typed error frame instead (section 7).
- **The schema.** The export writes `requery: "perViewer" | "shared"` per method, next to `readOnly` (ADR-0085), and
  the TypeScript generator carries it into `rpcMethodInfo`. The source types are not exported: entity names belong to
  the internal model, which ADR-0043 keeps off the wire, and hints name the method, whose wire name is already public.
  Applications without watched methods get a byte-identical schema and generated client. The events client is
  generated whenever watched methods exist, even without application topics.
- **SSE only in v1.** Hints and results travel on the client-event SSE stream. The resolver, expansion and throttling
  stay transport-neutral, so the connection adapters (ADR-0053) can follow.

### 6. `PerViewer` delivery (the default)

Delivery is throttled per node and per (method, partition), latest wins. W is the number of subscriptions of that key
on the node, counted as they are: hidden tabs, which hold their hints, count too.

- **Interval and spread.** The node sends the first hint at once. Later hints within the interval merge into one hint at
  the end of it, and that trailing hint is always sent, so the last re-query runs after the last commit. The interval is
  `max(100 ms, W / budget)`, with a budget of 20 re-queries per second per key. Each hint carries a spread window of
  `W / budget`, and on receipt each connection waits its own fixed fraction of that window, chosen when the connection
  opens, before it re-queries. A handful of viewers therefore re-query within about 100 ms of a commit, and a crowd
  re-queries evenly across the window instead of all at once.
- **The node budget.** A node also bounds its total: the lower of 200 re-queries per second and 0.5 database-seconds
  per second, the latter computed from the mean duration the node observes for each method, RPC calls included.
  Demand is hint deliveries × W over a sliding 5 s window; when it exceeds the budget, every key's interval is
  stretched by demand ÷ budget.
- **Backoff follows load, not the change rate.** On the server the interval grows with the audience and with the
  node's load. On the client the follow-up of an `onStale` call waits at least as long as the last call took, so when
  the database slows down, re-queries space themselves out. Together they cover both ways of falling behind: more
  viewers than the database should serve at once, and a refresh slower than the interval.
- **Logs.** "Candidate for `Shared`" when a key's own W pushes its interval past 5 s, and "node budget saturated",
  naming the top keys, when the node stretches.
- **The catch-up cursor.** Every frame on the client-event stream, keep-alives and `elarion.connected` included, carries
  the node's clock time `t` as its SSE `id:`. On reconnect, the browser's automatic one included, the server reads
  `Last-Event-ID` as the last time the client saw, preferring it over a `since` in the URL, because the browser's own
  retry reuses the original URL. A first subscription looks back 10 s, which covers the gap between a component's first
  fetch and its subscription going live. Each node remembers the last publication time per (source, partition) for 15
  minutes; a new subscription gets one catch-up hint per method whose sources were published after `since` minus a 5 s
  skew margin, and a hint for every method when `since` is older than that memory or than the node's listener. Catch-up
  hints are ordinary hints: spread (over at least 1 s for an "every method" catch-up), and counted against the budgets.
  After its own listener reconnects, the node sends a spread hint to every subscription, because publications may have
  been lost.
- **Reconnects.** A changed subscription set closes the `EventSource` and opens a new one (break-before-make); the
  catch-up cursor covers the gap. `onStale` fires on hints, catch-up hints included, and not on `elarion.connected`.
- **Buffering.** Requery frames coalesce per subscriber and key outside the 64-slot subscriber buffer: one pending hint
  per method, and one pending result per `Shared` key (section 7). A burst for one key cannot evict another key's
  trailing frame, and memory grows with subscriptions, not with events.
- **The client.** The generated events client gains `onStale(method, handler)`, typed to `perViewer` methods. The
  handler must return a promise that settles when the refetch finishes (`() => Promise<unknown>`); `void` does not
  compile, because the in-flight and spacing rules depend on it. The client keeps one call in flight per method and
  merges hints that arrive meanwhile into one follow-up. While the document is hidden it holds hints and calls once
  when the page is visible again; `document` is feature-detected, since the client also runs under Node. Generated
  runtime code imports no UI library (AGENTS.md), so the TanStack wiring is one line in the application:

  ```ts
  events.$client.onStale('billing.getDashboard', () =>
    queryClient.invalidateQueries({ queryKey: ['billing.getDashboard'] }))
  ```

  The example keeps `invalidateQueries`' default `cancelRefetch: true` on purpose: a fetch that started before the
  commit must be cancelled, because its success would clear the stale flag and swallow the hint. Watched queries get
  `staleTime: Infinity`, so TanStack's own refetches do not run next to the hints, and mutations do not invalidate
  watched queries themselves, because the hint is the invalidation.

### 7. `Shared` delivery

The server runs the query and pushes the result; viewers do not re-query.

- **Keys.** Each node keeps the latest result per (method, partition, canonical request) that has a subscriber on the
  node. The request is restricted at compile time to scalars, enums, `Guid`s, dates and times, and length-bounded
  strings, because it travels in the subscription URL and appears in access logs; the docs say so. The canonical key
  comes from deserializing the request with the method's contract, validating it and re-serializing it; an invalid
  request is dropped at subscribe. Frames echo the request string the client sent, so a client watching one method
  with two requests routes each result.
- **The computation.** A generated entry point per `Shared` handler runs telemetry (tagged as a shared computation,
  with no user), resilience and a 30 s timeout. It skips authorization, the feature gate and validation, which ran per
  viewer at subscribe, and the result cache. The computation runs in the partition's tenant scope, which
  `ITenantContext` honours without consulting the resolver.
- **No user.** In a computation any access to `ICurrentUser` throws, and the computation starts with `ExecutionContext`
  flow suppressed, so a dependency on the caller fails on the first run instead of shipping. The docs list the usual
  hidden dependencies: query authorizers, feature variants chosen per user, and culture. `Shared` requires the
  framework's `ClaimsPrincipalCurrentUser`, which can be seeded with that guard; startup fails when a `Shared` handler
  exists and another `ICurrentUser` is registered. The generator rejects `Shared` on a handler that injects
  `ICurrentUser` or declares resource requirements.
- **Generations.** Every hint for a key raises its generation. A computation reads the generation before it opens its
  snapshot. A key starts a computation at most once per `max(1 s, last duration)`: a hint that arrives earlier, or while
  a computation runs, only raises the generation, and one computation runs when the wait has passed. A result is pushed
  only when the hash of its serialized form changed, and a result over the 256 KB cap becomes a typed error frame. At
  most one computation per key runs on a node, and the last published result reflects the last commit. After the node's
  listener reconnects, every key's generation rises, and the recomputations are spread over one second.
- **Frames.** On subscribe the node sends an acknowledgement ("accepted, computing"), then the result, and later
  results with their generation as version. The client's version guard is per subscription instance and resets on each
  new subscription, because generations from different nodes, or from a key that was dropped and recreated, are not
  comparable. A failed `Result` is pushed as a typed error frame with the method's error contract (ADR-0080). On an
  exception or timeout the node keeps the last good result and retries with backoff from 1 s to 30 s; when nothing has
  ever succeeded, it pushes the generic unexpected-error frame (ADR-0086). A fallback frame ("stop expecting results")
  and a resubscribe frame complete the set.
- **The client.** `onResult(method, request, handler)` hands results and errors to the application. For TanStack,
  `events.$client.awaitResult(method, request)` serves as the `queryFn`, so watched `Shared` methods are not fetched
  over RPC in the browser, where an unversioned response could overwrite a newer push. It falls back to RPC on a
  fallback frame, or when no acknowledgement arrives within 10 s; after an acknowledgement it waits up to the
  computation timeout plus 5 s. After a fallback the query stays a plain RPC query until the component remounts. Where
  no `EventSource` exists (SSR, Node), it uses RPC.
- **Lifetime.** The first subscriber of a key triggers the first computation, and every new subscriber is greeted with
  the current result. When the last subscriber leaves, the key lingers for 5 s and is dropped.
- **Re-authorization.** A `Shared` subscription is authorized once per viewer, not per computation, so the node
  re-authorizes it in place on a jittered 10-minute timer, in a dispatch scope seeded with the principal captured at
  subscribe. A denied subscription gets the fallback frame. At subscribe time the transport also captures the
  authentication's expiry where one exists (in ASP.NET, `AuthenticationProperties.ExpiresUtc` or the `exp` claim).
  When it passes, the node sends a resubscribe frame; the client reconnects after a jitter in [0, 30 s), so the new
  request carries a renewed cookie, and falls back to RPC only if authentication then fails. Active users with sliding
  authentication stay live.
- **Fan-out.** Each node computes for its own subscribers, so a change costs at most one computation per key on each
  node, whatever the number of viewers. Results never cross nodes, so PostgreSQL's notification payload limit does not
  apply. Computing on a single node would save the other nodes' queries but needs every live connection routed to it
  (ADR-0050), which is left as a later option.

### 8. Scale: ten nodes on one PostgreSQL

- **`NOTIFY` rate.** At most one broadcast round per node per 100 ms (10 per second, 100 for ten nodes) whatever the
  write rate, and none while the cluster is idle. A round is one `NOTIFY` unless an oversized payload has to be split.
  Each is a small autocommit statement on the broadcaster's connection, outside the application's transactions. This
  matters because PostgreSQL holds a cluster-wide lock from the commit of a transaction that issued `NOTIFY` until the
  transaction ends, so a `NOTIFY` inside every writing transaction would serialize the application's writes.
- **Listeners.** Every node receives every publication, as with today's client events, and looks up each source in
  it.
- **Query load.** `PerViewer` costs at most the node budget: 200 re-queries or 0.5 database-seconds per second per
  node, whichever comes first. A key's audience on one node re-queries at most 20 times per second. `Shared` costs at
  most one running computation per key per node, at most one per second; the number of viewers changes only the number
  of frames.
- Past this tier the broadcaster seam is replaced, as ADR-0043 describes. The publication format stays the same.

### 9. Operations

- **Kill switch.** `Enabled` (read through `IOptionsMonitor`) stops publications and hints while subscriptions stay
  valid, so screens go stale but stay correct and RPC still works. `Shared` subscriptions get the fallback frame and new
  ones are dropped the same way; catch-up hints are suppressed. Switching it back on is treated like a listener gap: a
  spread hint to every `PerViewer` subscription, while `Shared` screens resume when they remount. Per-method
  configuration overrides the budget, floor and minimum interval. The mode stays a compile-time choice, because
  `onStale` and `onResult` differ.
- **Telemetry.** On the package meter `Elarion.ClientEvents`, with instruments named `elarion.requery.*`:
  publications and `NOTIFY`s sent, splits and collapses, failed sends, hints delivered, the current interval per key,
  budget saturation, `Shared` computations (a duration histogram in seconds with semconv buckets, and an outcome tag),
  pushes and bytes, frames coalesced, and the notification-queue gauge. The method name may be a tag, since the catalog
  bounds it; the tenant never is, only on spans and logs.

### 10. Analyzers

Two diagnostics. Both live in a diagnostic analyzer rather than the generator, because the generator runs on every
keystroke and must stay cheap. They ship with the `Elarion` package's bundled analyzers, so they run in every assembly
that writes or reads, module assemblies included; analyzer assets are not transitive, so the producer package could not
carry them.

- **An unreported set-based write is a build error.** An `ExecuteUpdate` or `ExecuteDelete` (sync or async) on an
  entity type `T` needs an `IRequeryHints.Changed<T>()`, or `Changed<T>(transaction)`, in the same method; a code fix
  inserts the report after the statement. The analyzer reads `T` from the method's generic argument, so precompiled
  queries are covered. It skips framework-owned types and types the compilation excludes with `ExcludeFromRequery()`
  where it can see that configuration. For an exclusion declared in another assembly, the report is harmless, since a
  report of an excluded type is dropped like its tracked writes, or the diagnostic is suppressed with a justification.
  It is an error, not a warning, because the failure it prevents is a silently stale screen (AGENTS.md prefers a clear
  compile-time error over a silent run-time gap). Applications that do not use the feature never see it: it is enabled
  solution-wide by the MSBuild property `ElarionRequeryHints`, set for example in `Directory.Build.props`, which the
  producer's docs require.
- **An undeclared source is a warning.** The analyzer warns when the body of a `[RequeryOn]` handler reads an entity
  type it does not declare: a `DbSet<T>` or `Set<T>()` access, or a navigation to such a type. It does not follow calls
  into services, module APIs, raw SQL or views, so the declaration stays the source of truth and the warning is only a
  reminder. It does not report a declared source that the body never reads, because queries often read through a
  helper.

Raw SQL cannot be checked this way; reports for it stay the developer's responsibility, as section 2 says.

### 11. Packages

- `Elarion.Abstractions`: `RequeryOnAttribute<TSource>`, `RequeryAttribute` with `RequeryMode` and
  `RequeryPartitions`, `RequerySourceAttribute`, `WritesRequerySourceAttribute<TSource>`, `IRequeryHints`,
  `ClientEventScope.Tenant`, `AuthorizationContext.IsSubscription`.
- `Elarion` and its generator: the method-to-sources catalog in the generated module registration, the `requery` mode
  in the handler client metadata, the `Shared` computation entry point and its checks, the no-op `IRequeryHints`, and
  the two analyzers with their code fix.
- `Elarion.EntityFrameworkCore`: the requery model annotations and their entity-configuration extensions.
- `Elarion.ClientEvents`: the change set, the publication, `IRequeryPublications` for replacement producers, the
  merger, the `elarion.requery` subscription, the expansion, `PerViewer` throttling and budgets, the catch-up cursor,
  and `Shared` computation.
- `Elarion.ClientEvents.AspNetCore`: the SSE endpoint's side: `t` as the `id:` of every frame, reading
  `Last-Event-ID` and `since`, the acknowledgement, fallback and resubscribe frames, and capturing the authentication
  expiry at subscribe.
- `Elarion.ClientEvents.PostgreSql`: splitting oversized publications across `NOTIFY` payloads, the listener's
  no-transaction rule, and the notification-queue gauge.
- `Elarion.ClientEvents.EntityFrameworkCore` (new): the producer. It is a sibling package so that
  `Elarion.EntityFrameworkCore` does not depend on client events.
- `Elarion.Sql`: the internal commit, rollback and savepoint hooks on `SqlUnitOfWork` and on the `ISqlTransaction`
  implementation.
- The TypeScript generator: `rpcMethodInfo.requery`, `onStale`, `onResult` and `awaitResult`, the catch-up cursor and
  break-before-make reconnects.

### 12. Delivery plan and acceptance criteria

The implementation lands as stacked PRs, each targeting the previous one:

1. contracts, the catalog, the schema value, the generator checks, the analyzers and the startup checks;
2. the producer: change set, EF sibling package, SQL-tier hooks, `TransactionScope`, model annotations, publication,
   merger and `IRequeryPublications`;
3. `PerViewer` delivery: the local-delivery decorator, throttling and budgets, the catch-up cursor, per-key
   coalescing, `ClientEventScope.Tenant`, `IsSubscription` with the ADR-0085 edit, and the TypeScript `onStale`;
4. `Shared` delivery.

The trigger and logical-replication producers are docs recipes, written once step 2 has landed.

Acceptance:

- **Gates, as deterministic tests in `tests/Elarion.Tests`:** at most 10 broadcast rounds per second per node under
  sustained writes (a round being one `NOTIFY`, splits of an oversized payload aside) and none while the cluster is
  idle, counted by a test listener on Testcontainers PostgreSQL; and the node
  budget holding for count and time under a synthetic herd, with `FakeTimeProvider`.
- **Benchmark, recorded in the PR:** the writer's commit latency at p50 and p99 with and without the producer, in
  `tests/Elarion.Benchmarks`. The benchmarks do not run in CI, so they record rather than gate.
- **Test matrix:**
  - Testcontainers PostgreSQL: tracked writes, cascades, reported `ExecuteUpdate`/`ExecuteDelete` in autocommit and
    inside a transaction, rollback, savepoints, join entities, `TransactionScope`, `UseTransaction`, excluded types,
    the untracked-transaction warning, and the SQL-tier hooks (unit of work and `ISqlTransaction`) and sources;
  - analyzer tests: the unreported-write error and its code fix, framework and excluded types, the opt-in property,
    and the undeclared-source warning;
  - a two-node fan-out over a real `LISTEN`;
  - `FakeTimeProvider` tests for the throttle, the budgets, the catch-up cursor and its horizon;
  - the `Shared` generation race: a hint during a computation produces exactly one more;
  - the `ICurrentUser` guard and the startup failures;
  - TypeScript client tests: in-flight merge, duration spacing, hidden page, `Last-Event-ID`, the acknowledgement and
    fallback of `awaitResult`, and the per-subscription version guard;
  - generator tests with the incremental cache test, and byte-identity tests;
  - NodeNext type-checking of the emitted client.

## Consequences

- The reader states its dependency once. Writers added later, such as jobs, bulk paths, other modules, other hosts and
  cascades, are covered without changes.
- Integration events and projections that exist only to refresh a screen go away. The integration plane keeps the
  events that carry intent.
- **Over-notification.** Any write to a source in the partition re-queries every `PerViewer` viewer and recomputes
  every `Shared` key, including writes to columns the query does not read and to rows outside its filter. The budgets
  bound the cost. A busy key can still cause a second query when a screen mounts, if its sources changed within the
  catch-up window.
- **Freshness gives way first under load.** A large audience on one node, or a saturated node, waits longer between
  refreshes; a small audience keeps about 100 ms however busy its tenant is. Freshness degrades before the database
  does, which is the intended order.
- **Activity is visible.** A viewer learns, at the resolution of the interval, that some row of a source changed in
  their tenant, including rows they cannot read. Writes to a type that is not tenant-scoped are visible across
  tenants. Hints carry no id and no content. An application for which the timing of changes is itself sensitive keeps
  explicit, resource-scoped client events.
- **`Shared` revocation is bounded, not immediate.** A revoked permission takes effect within 10 minutes plus jitter,
  an expired login within its expiry plus 30 s. Permissions carried in claims change only with a new token, and logout
  counts only when the host's authorizer checks a session store. Hints do not have this gap, since every re-query runs
  the full pipeline.
- **Delivery is weaker than the hand-built path.** The hand-built path goes through the outbox and survives a crash of
  the producing node after commit. A publication lives in memory, so a crash in that window loses it for viewers on
  other nodes; they stay stale until the next change. An application that cannot accept this sets a
  `refetchInterval` as a safety net.
- **Set-based and raw writes cost one line.** Every `ExecuteUpdate` and `ExecuteDelete` carries a `Changed<T>()` next
  to it, which the build enforces and a code fix writes. Raw SQL, the AOT SQL tier and stored procedures are seen only
  when reported, and nothing checks those reports.
- **Known completeness gaps.** A report inside a transaction opened with raw ADO.NET on a bare connection publishes
  early, which is the one known case where a hint can be swallowed; framework transactions (EF, the unit of work and
  `ISqlTransaction`) are covered, and other visible but untracked transactions log a warning. Writes by triggers,
  other applications and migrations are not seen unless a replacement producer runs. A writing host that registers
  neither the feature nor the producer goes undetected.
- **The catch-up cursor uses wall clocks.** Clock skew between nodes beyond the 5 s margin, or a backward clock step,
  can make a catch-up miss.
- **Docs-only hazards in v1.**
  - A watched handler that runs several statements reads each in its own snapshot under Read Committed, so a commit
    between them can make its panels disagree. Such a handler should read in one read-only `REPEATABLE READ`
    transaction; an opt-in may follow.
  - A re-query routed to a lagging read replica (ADR-0037) can read the old state and swallow the hint. A consistency
    token in the hint is the recorded follow-up.
  - Existing client-event topics keep the subscriber buffer's drop-oldest behaviour across topics; a gap event is a
    separate follow-up.
- Schemas gain a `requery` mode on watched methods, and the generated client gains `onStale`, `onResult` and
  `awaitResult`; other methods and applications without watched methods stay byte-identical. ADR-0085's sentence on
  `HandlerType` changes.

## Rejected alternatives

- **Polling a keyset query.** It stays the recommended choice when seconds of staleness are fine. As the mechanism here
  it costs one query per watcher per interval even when nothing changed, and the interval is the freshness.
- **Per-writer topics (today's way).** The dependency lives at the writers, so a forgotten event leaves the screen
  silently stale, and events that only refresh a screen burden the integration plane. They remain the right tool for a
  hint that carries an id or intent, such as a detail screen's.
- **Intercepting `ExecuteUpdate`/`ExecuteDelete` with a tag injected at query compilation,** read back by a command
  interceptor on every execution. None of the frameworks the second research note covers intercepts set-based statements; all document the
  gap. EF Core lists interceptors for "manipulating" operations and warns that expression-tree interception is easy to
  get wrong. The tag depended on EF's query cache and on scanning SQL comments, missed precompiled queries, needed a
  spike before acceptance and a test on every EF Core upgrade, and was the kind of EF-internals surface ADR-0090
  removed. A reported line, enforced by the build, does the same job.
- **Detecting writes at the handler boundary.** Commands could declare or have the generator infer the sources they
  write, and the pipeline would publish after the unit of work commits, as Ash notifiers fire on declared actions. It
  would be tier-neutral and AOT-safe, but many Elarion writes run outside the command pipeline (scheduled jobs,
  method-form consumers, actors, hosted services), and the dependency would move back to the writers, which is the
  problem this ADR exists to fix.
- **A report for every write, as Phoenix broadcasts explicitly.** Tracked writes are caught automatically and
  reliably; requiring reports for them too would bring back the forgetting problem with no way to check it at build
  time.
- **Generated C# interceptors at `Execute*` call sites.** They could make the report automatic, but EF's precompiled
  queries intercept the same call sites, and two interceptors on one call site do not compile. Left open; the code fix
  makes the explicit line cheap.
- **Database triggers as the default producer.** Statement-level triggers into a change table see every write and are
  a documented replacement producer (section 2). As the default they would add schema objects to keep in step with the
  model, tenant logic in SQL, an extra insert per write statement, a gap-safe poller and no SQLite path, for a hint
  that only needs table names. A trigger that calls `pg_notify` directly is rejected outright: it takes the
  cluster-wide lock at commit and serializes every watched write, before any throttling can apply.
- **Logical replication as the default producer.** It sees every committed change, including raw SQL, cascades and
  other applications, and is a documented replacement producer. As the default it needs `wal_level = logical` (a
  restart, and often restricted on managed databases), a replication slot that retains WAL while its reader is down (a
  known way to fill a disk), one elected reader and a publication per table: change-data capture beyond this tier.
- **Exporting the source types and subscribing per type.** This exposes the internal model to the browser and would
  need an authorization vocabulary per entity type. The query method already has the right one.
- **Inferring the sources at run time** by recording which types a handler's queries touched. Sources vary by branch
  and are unknown until each branch has run. Elarion prefers declarations checked at build time.
- **Generic row-change domain events (`EntitiesAdded<T>`, `EntitiesUpdated<T>`).** See "Context".
- **Durable hints through the outbox.** An outbox row and a delivery for every write transaction, for a delivery that
  is at most once at the last hop anyway.
- **Separate `Immediate` and `Throttled` modes,** with a 1 s floor on a throttled default. A small application would
  need an annotation on every handler to get immediate updates, and the floor duplicates what the client's in-flight
  merge already does. The audience budget separates the two regimes by itself.
- **Backoff driven by the change rate** (doubling the interval while hints keep coming, up to 30 s). It made a small
  audience pay for a busy tenant, it has no precedent in the systems the research note covers, and the budgets bound
  the same cost without it.
- **A fixed interval or a fixed jitter.** A fixed interval's cost grows with the audience; a fixed jitter spreads a
  herd over the same window whatever its size.
- **Publishing only the types the host's own modules watch.** A worker host that enables other modules would then
  publish nothing for the dashboard's sources. Publishing every type, minus model-declared exclusions, makes the
  answer independent of host configuration; per-host exclusion options were rejected for the same reason.
- **Opening the new stream before closing the old one (make-before-break).** It needs two SSE connections per tab on
  every navigation, which exhausts HTTP/1.1's per-origin connection budget, and it counts viewers twice. The catch-up
  cursor closes the gap instead.
- **All-or-nothing resolution for requery subscriptions.** One closed feature flag or revoked permission would
  silently stop every client event on the tab, and the client would retry the failing set indefinitely.
- **An unauthenticated `ICurrentUser` in `Shared` computations.** A filter that depends on the user would quietly
  return nothing in the computation while RPC returns rows, so the screen would flip between the two. A throwing guard
  fails on the first run instead.
- **Serving the shared result to re-queries instead of pushing it.** Authorization would stay per request and nothing
  new would cross the wire, but every change would still cost one request per viewer and an extra round trip, and
  removing those is what the shared mode is for. Periodic re-authorization bounds the revocation gap instead.
- **Sharing results through the handler cache (ADR-0004), evicted on each hint.** A computation that started before a
  commit and finishes after the eviction stores the old state as current. Reading HybridCache's source suggests that a
  run overlapping a tag invalidation is stored as valid (not yet confirmed by a test), and its stampede protection is
  per instance. The shared mode's generation is that cache with the race closed.
- **Computing a shared result on one node and broadcasting it through `NOTIFY`.** Results routinely exceed
  PostgreSQL's 8000-byte notification payload, and every broadcast would take the notification lock.
- **Suppressing the writer's own echo.** It only works when a merged publication comes entirely from that writer's
  changes, which is rare exactly on the busy keys where it would matter. Mutations simply do not invalidate watched
  queries themselves.

## Before this is accepted

- Measure the defaults with a benchmark in `tests/Elarion.Benchmarks` and record them here: the 100 ms batching window,
  the 100 ms floor, the per-key budget of 20 re-queries per second, the node budget of 200 re-queries and 0.5
  database-seconds per second, the 5 s demand window, the `Shared` minimum interval of 1 s, the catch-up lookback,
  horizon and margin (10 s, 15 minutes, 5 s), and the 10-minute re-authorization period.
