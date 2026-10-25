# Live dashboards over a relational database: re-query hints, fan-out and load shaping

- Date: 2026-10-25
- Status: Research input for [ADR-0091](../0091-query-sources-and-requery-hints.md). This is not a decision. Section 8
  was written against the ADR's first draft (a 30 s backoff cap and a fixed 250 ms jitter); the ADR has since adopted
  most of its recommendations.
- Method: primary sources only: vendor documentation, source code pinned to a commit, specifications, and engineering
  posts by the vendor. Every claim links its source (the link labels resolve in [Sources](#sources)). Text marked
  **Inference** is my reading, not something the source states. Quotes are short phrases; everything else is
  paraphrased.

## Summary

1. **Every live-query system surveyed shares work between viewers instead of re-running per viewer.** Hasura merges
   similar subscriptions into one SQL statement per batch and polls every second. Meteor runs one observe driver per
   identical cursor per server process. Convex single-flights and caches query results. Electric shares a shape log
   and relies on CDN request collapsing. PostGraphile v4 is the exception: it re-runs per subscriber, warns about a
   thundering herd, and recommends live queries for small user bases.
2. **What gets shared is always "the same query in the same authorization context".** Hasura groups by role and
   session variables. Convex drops the identity from its cache key only when the query never read auth. Electric
   needs `Vary: Authorization` for per-user responses. Per-user projections cannot be shared; per-tenant projections
   can.
3. **Hints are the safer default; push state only when the payload is identical for the whole audience and the
   audience is the authorization boundary.** TanStack Query's maintainer prefers invalidation. Supabase caches
   broadcast authorization for the life of a connection, so a revoked user keeps receiving pushed data until the
   token changes. Phoenix encodes a broadcast once unless per-socket customization forces N encodings. A NOTIFY
   payload must stay under 8000 bytes.
4. **The ADR's in-flight rule matches established practice.** Allowing one run in flight and merging later changes
   into one follow-up is what Meteor's polling driver does. Convex states the exact condition: a request may reuse a
   result computed at an earlier snapshot only after checking that no later commit touched what that result read.
5. **TanStack's `invalidateQueries` cancels an in-flight fetch by default, and a successful fetch clears the
   invalidated flag.** If cancellation were turned off for hint-driven refetches, a fetch that started before the
   commit could swallow the hint.
6. **No surveyed system lowers freshness because the change rate goes up.** They bound cost either by sharing
   (Hasura cohorts, Meteor multiplexers, the Convex cache) or with a fixed throttle (PostGraphile 500 ms on both
   edges, Turbo 500 ms on the server plus 150 ms on the client, Hasura 1 s). The ADR's 30 s backoff cap has no
   precedent, and it makes a small audience pay for a busy tenant.
7. **Jitter has to cover the whole window.** AWS found that backoff without jitter still clusters calls, and that
   "full jitter" (random over the whole window) does the least work. A fixed 250 ms jitter spreads a herd of N over
   250 ms, whatever the interval is.
8. **Turbo's server-side refresh debounce only merges refreshes from one request.** It is per thread and per request
   id (0.5 s, trailing edge), so refreshes caused by different users are merged only by the 150 ms client debounce.
   Turbo and Laravel both let the client that made a write skip the echo of its own change.
9. **Publishing after commit is the common baseline.** PostgreSQL NOTIFY is transactional. Laravel offers
   `ShouldDispatchAfterCommit`. Turbo's model broadcasts hang off `after_*_commit` callbacks. Meteor's DDP `updated`
   message tells the client when a method's writes are visible.
10. **PostgreSQL makes every commit that issued a NOTIFY take one heavyweight lock, held until after the commit, and
    notifications do not survive a crash** (both from the source code). Sending NOTIFY in batches from a separate
    autocommit connection, as the ADR does, is consistent with that.
11. **A shared server-side cache needs a generation guard.** Convex discards a result older than the cached one.
    .NET HybridCache's stampede protection is per instance, and its invalidations do not reach other servers'
    in-memory caches. Reading its source, I believe a factory run that overlaps a tag invalidation can be stored as
    valid (unverified; see Open questions).
12. **Incremental maintenance moves the cost to the writers and serializes them.** pg_ivm takes an `ExclusiveLock` on
    the view after a base table changes and says it is not effective when base tables change often. A hand-written
    summary row has the same hot-row wait under Read Committed, and under Repeatable Read it causes serialization
    failures.

## 1. Live-query systems and how they avoid re-running per client

### 1.1 Hasura live queries

- A live query sends the latest result, not each change, and refreshes every second by default ([hasura-livequery]).
- **Multiplexing.** Hasura merges subscriptions into one SQL query when they share a role, have identical selection
  sets, and differ only in scalar argument values. Each row of the result belongs to one subscription. Batches hold
  100 subscriptions by default (`HASURA_GRAPHQL_LIVE_QUERIES_MULTIPLEXED_BATCH_SIZE`), so 1,000 similar subscribers
  cost about 10 SQL statements ([hasura-exec]).
- **Cohorts.** Subscriptions with the same query and session variables form a cohort. A cohort's members share one
  part of the SQL response and are updated together, so adding members costs no extra database time
  ([hasura-exec], [hasura-perf]).
- Hasura diff-checks each response and pushes it only when the data changed ([hasura-exec]).
- **Tuning.** `HASURA_GRAPHQL_LIVE_QUERIES_MULTIPLEXED_REFETCH_INTERVAL` defaults to 1 s. A longer interval means
  less database load and more latency. The docs advise a smaller batch when the SQL takes longer than the interval
  ([hasura-perf]).
- **Why Hasura polls.** The architecture notes say the team tried LISTEN/NOTIFY, which needs triggers on every table
  and loses events when the consumer restarts, and WAL, whose replication slots are expensive and often unavailable
  on managed databases. Mapping change events onto complex queries also turned out to be impractical, so they have
  "fallen back to interval based polling" ([hasura-arch]). Their benchmark ran 5,000 live queries on a 1 vCPU instance, and
  one million live queries with PostgreSQL at about 28 % load ([hasura-arch]).
- Streaming subscriptions are the cursor-based alternative: each new row is sent once, and the cursor must be unique
  and sortable ([hasura-streaming]).
- **Inference:** Hasura pays once per cohort per interval, independent of the number of watchers and of the write
  rate. In Elarion's terms it is tier 3 on a timer instead of on hints.

### 1.2 Meteor

- `ObserveMultiplexer` lets one observe driver serve many identical observe handles. A late handle gets its initial
  documents from the shared cache ([meteor-multiplex]).
- The lookup key is the serialized cursor description plus the `ordered` flag; an existing multiplexer with the same
  key is reused ([meteor-conn], L1008–1029).
- Driver order is change streams, then oplog, then polling, and can be configured ([meteor-conn]). The oplog driver
  sees changes quickly, including those from other clients, but needs a replica set. Queries it cannot handle fall
  back to poll-and-diff, where changes from another process can take up to 10 seconds to appear ([meteor-oplog]).
- **Polling driver.** It polls every 10 s by default and throttles poll scheduling to 50 ms. A write notification
  from the same process schedules a poll unless one is already scheduled and not yet started. That counter drops
  when a poll starts, so a write that lands during a poll schedules exactly one more poll ([meteor-polling],
  L13–14, L58–78, L107–112, L133–134). The writes are tracked through a write fence and marked committed only after
  the poll that reflects them ([meteor-polling], L73, L182). This is what drives the DDP `updated` message, which
  lists the methods whose writes "have been reflected" in the data sent ([ddp]).
- **Per-connection merge.** The default `SERVER_MERGE` strategy keeps a copy of every document a connection is
  subscribed to, so the server can send deltas. The other strategies give up that memory and the deltas with it
  ([meteor-api]).
- **Inference:** by default Meteor computes once per node for each identical query and delivers per connection. That
  is tier 3 at node scope, and it is the cost shape that fits Elarion's "every node receives every publication".

### 1.3 Convex

- **Read sets.** A query records the index ranges it scans. The subscription manager holds the read sets of all
  active subscriptions and walks the transaction log once. When a commit overlaps a read set, the query is re-run and
  the new result pushed ([convex-how]).
- Each result is tied to a timestamp, and all queries in a client's set are kept at the same timestamp, so the UI
  never mixes snapshots ([convex-how], [convex-realtime]).
- **Cache.** The docs say the same query with the same arguments is served from cache, and that queries must be
  deterministic for this to work ([convex-query]). The source adds the full key: deployment, function path,
  arguments, identity, pagination journal and visibility. The identity is left out when the query never read
  `ctx.auth` ([convex-cache], L147–258).
- **Single-flight with a snapshot check.** A running computation is stored as a `Waiting` entry along with the
  timestamp it runs at. A later request joins it only if that timestamp is not newer than the one the request needs.
  The request then validates the shared result: if any commit since the result's timestamp overlaps its read set,
  the result is discarded and recomputed ([convex-cache], L273–282, L924–948, L770–800).
- **Stale-write guard.** When a result is stored, an existing entry with a higher timestamp wins and the older
  result is dropped ([convex-cache], L500–502, L1059–1086).
- **Inference:** Convex is tier 3 with exact invalidation. Elarion's type-level sources are a coarse read set, so a
  shared Elarion result cannot be validated after the fact. It has to treat any hint that arrives after a run started
  as invalidating that run.

### 1.4 Supabase Realtime: Postgres Changes and Broadcast

- **Postgres Changes** are processed on a single thread to preserve order, so larger compute add-ons barely raise
  throughput. Each change is checked against every subscriber's authorization: 100 subscribers means 100 checks.
  Above roughly 3,000 concurrent subscribers on the same changes, the docs say to use Broadcast, which "sends each
  change once" ([supabase-pgchanges]). The published table shows, for example, a micro instance with RLS and 500
  clients handling about 30 database changes per second ([supabase-pgchanges]).
- The docs call Broadcast the recommended method "for scalability and security" ([supabase-subscribing]). To
  broadcast from the database, a trigger calls `realtime.broadcast_changes()` into a private channel guarded by RLS
  policies on `realtime.messages` ([supabase-subscribing]). The message is inserted into `realtime.messages`, and
  Realtime reads it from the WAL through a publication. The table is partitioned by day and keeps messages about
  three days ([supabase-broadcast]). **Inference:** because delivery comes from the WAL, only committed inserts are
  broadcast. The docs do not say so explicitly.
- **Authorization** is evaluated when a client connects and joins, then cached for the connection. It is evaluated
  again only when the client sends a new token, so revoked access keeps working until the JWT expires
  ([supabase-auth]).
- **Inference:** moving from Postgres Changes to Broadcast is the same move as from tier 2 to tier 3: the
  per-subscriber check moves to subscribe time. Elarion's hint keeps the per-subscriber check on the re-query. That
  is the cost Supabase removes, and it is also why a hint stays safe with an authorization that was only checked at
  subscribe time.

### 1.5 Electric

- A shape is a table with an optional `where` clause and column list. Many clients can sync the same shape. Shapes
  cover a single table ([electric-shapes]). With unoptimized `where` clauses, throughput falls as shapes are added
  (1,400 changes/s at 10 shapes, 140 at 100). Optimized clauses stay near 5,000 row changes/s regardless of count
  ([electric-shapes]).
- The HTTP API syncs from an offset. A `live=true` request is held open until new data arrives or a timeout passes.
  Responses carry cache headers so that proxies and CDNs can cache them. CDNs with request collapsing forward only
  one of many identical live requests to the origin, and Electric relies on that to serve large numbers of clients
  ([electric-http]).
- Electric authorizes requests through a proxy or a gatekeeper token. Per-user responses must carry
  `Vary: Authorization` or `Vary: Cookie` so that caches keep them apart ([electric-auth]). **Inference:** collapsing
  helps only viewers whose requests are identical including the authorization part. Per-user auth defeats it;
  per-organization shapes keep it.

### 1.6 PostGraphile `@live` (v4)

- PostGraphile re-runs the whole query when a relevant change arrives. The first-party plugin reads a logical
  replication slot and checks it every `LD_WAIT` (125 ms). It needs `wal_level = logical` and wal2json
  ([postgraphile-live]).
- `LIVE_THROTTLE` defaults to 500 ms and fires on both the leading and the trailing edge ([postgraphile-live]).
- The docs warn about a "thundering herd" when many subscribers watch the same data. They recommend live queries for
  small user bases such as internal tools, and plain subscriptions for tens of thousands of users. Many small
  queries are better than one large one ([postgraphile-live]).
- **Inference:** this is the closest analogue to tier 2, and its documented limit is the limit tier 2 will have.

### 1.7 Comparison

| System | Detects change by | Shared between viewers | Pushes | Rate control |
| --- | --- | --- | --- | --- |
| Hasura | polling | one SQL per batch; cohort = role + session variables | state, only when changed | 1 s interval |
| Meteor | change streams, oplog, or poll-and-diff | one driver per identical cursor per process | per-connection deltas | 10 s poll, 50 ms throttle |
| Convex | read set vs. commit log | cached result per (function, args, identity if read) | state | per commit, exact |
| Supabase Postgres Changes | WAL | nothing; authorization per subscriber | row changes | single thread |
| Supabase Broadcast | trigger, message table, WAL | one message per change | application payload | not documented |
| Electric | logical replication | shape log, CDN request collapsing | row changes | long poll |
| PostGraphile v4 | logical decoding | nothing; re-run per subscriber | state | 500 ms, both edges |
| ADR-0091 draft | EF interceptor, entity type | one publication per node | hint | 100 ms per node; 1 s per (method, partition) with backoff |

## 2. Broadcast and fan-out frameworks

### 2.1 ASP.NET Core SignalR and Azure SignalR Service

- `IHubContext` can be injected into controllers, middleware and other services to send from outside a hub. There is
  no caller there, so `Caller`, `Others` and `ConnectionId` are unavailable ([signalr-hubcontext]).
- **Groups** are managed by the application. Membership is lost on reconnect and lives in memory, and members cannot
  be counted once the app is scaled out. Groups "are not a security feature": when a permission is revoked, the app
  must remove the connection itself ([signalr-groups]).
- **Scale-out** needs sticky sessions unless the app runs on one server, uses Azure SignalR Service, or uses
  WebSockets only with `SkipNegotiation`. With a Redis backplane, the app has to scale out with the number of
  clients even when it sends few messages ([signalr-scale]). If Redis is down, messages are lost, not buffered
  ([signalr-redis]).
- **Backplane limits.** The older SignalR 2 page states it most clearly: the backplane forwards every message to
  every node, so it can become the bottleneck. It suits server broadcast "because the server controls the rate", can
  bottleneck when the message rate grows with the number of clients, and is not recommended for high-frequency
  real-time apps ([signalr2-scaleout]).
- Per-connection buffers default to 32 KB, and raising them lowers the number of concurrent connections a server can
  hold ([signalr-security]).
- **Azure SignalR Service.** Outbound bandwidth for a broadcast is connections × message size ÷ send interval, and a
  message under 1 KB costs as much as a 1 KB message. At unit 1, a broadcast reaches about 2,000 outbound messages
  per second ([azure-signalr-perf]).
- **Inference:** pushing a result costs payload × watchers × rate, while a hint keeps the payload near the minimum.
  If tier 3 pushes results, the payload size becomes a design parameter.

### 2.2 Rails Turbo 8: `broadcasts_refreshes`

- Turbo 8 added a `refresh` stream action, and pages apply it by morphing ([turbo-handbook]).
- `broadcasts_refreshes` registers `after_create_commit` and `after_update_commit` callbacks that call
  `broadcast_refresh_later`, and an `after_destroy_commit` callback that calls `broadcast_refresh` at once.
  `broadcasts_refreshes_to` uses `after_commit` ([turbo-broadcastable], L209–219).
- `broadcast_refresh_later_to` runs through a debouncer keyed by the stream name and the request id, and stored per
  thread ([turbo-broadcasts], L70–76, L107–109). The default delay is 0.5 s, and each call cancels the pending task
  and schedules a new one, which makes it a trailing-edge debounce ([turbo-debouncer], [turbo-threaddebouncer]).
- In the browser, refreshes are debounced for 150 ms on the trailing edge. A refresh is ignored when its request id
  is one of the client's own recent requests, when a navigation is already running, or when the URL is a different
  page ([turbo-session], L42–48, L108–117, L178–181).
- **What is debounced, and over what window:** the server merges refresh broadcasts for one stream that come from the
  same thread and request (or job), over 0.5 s. The client merges everything it receives over 150 ms.
  **Inference:** refreshes caused by different requests are not merged on the server.
- **Inference:** dropping a refresh while a navigation is running is the in-flight hazard from section 5.1 if that
  navigation's request started before the commit.

### 2.3 Laravel broadcasting

- Broadcasts run as queued jobs. `ShouldBroadcastNow` uses the sync queue instead ([laravel-bc]).
- A broadcast dispatched inside a database transaction can be processed before the transaction commits. The fix is
  the queue connection's `after_commit` option, or `ShouldDispatchAfterCommit` on the event ([laravel-bc-tx]).
- `toOthers()` skips the sender's own socket, which the client identifies with an `X-Socket-ID` header. With
  `ShouldRescue`, a failed broadcast is reported without failing the request ([laravel-bc]).

### 2.4 Phoenix PubSub and LiveView

- `broadcast` reaches the whole cluster, `local_broadcast` only the current node, and `direct_broadcast` one named
  node. The `broadcast_from` variants skip the sender. The default adapter uses distributed Erlang; a Redis adapter is
  available ([phoenix-pubsub]).
- On channels, events that are not intercepted are pushed straight to clients. Intercepting an event lets each
  socket customize it, but the message is then "encoded N times instead of a single shared encoding"
  ([phoenix-channel]).
- LiveView subscribes in `mount` only once the socket is connected and receives broadcasts in `handle_info`. Every
  LiveView is a server process holding its assigns, and streams keep large collections off the server ([liveview]).
- **Inference:** in LiveView, computing once means computing in one publisher process and broadcasting the result;
  computing per socket means each LiveView re-queries in `handle_info`. The docs do not prescribe either.

### 2.5 PostgreSQL LISTEN/NOTIFY as the backplane

- Notifications are delivered only after the transaction commits. Identical payloads sent in one transaction are
  folded into one, but notifications from different transactions never are. A payload must be shorter than 8000
  bytes. The queue holds 8 GB, and a NOTIFY fails at commit once it is full. A listener that stays inside a long
  transaction blocks cleanup. Notifications arrive in commit order ([pg-notify]).
- Every transaction that issued a NOTIFY takes a heavyweight lock on "database 0" just before commit and holds it
  until after the commit, which serializes all of those commits ([pg-async], L1293–1310). Notifications are not
  expected to survive a database crash ([pg-async], L34–36).

## 3. Client patterns: TanStack Query

- **Invalidate or write to the cache.** TkDodo, who maintains TanStack Query, prefers having the server send an
  entity descriptor and the client invalidate it, because events for data nobody is looking at then cost nothing.
  Partial updates with `setQueryData` fit small, frequent changes to large data. They do not handle additions or
  deletions, and they get awkward when several query keys hold the same entity ([tkdodo-ws]).
- When pushes keep the data fresh, he suggests a high `staleTime`, such as `Infinity` ([tkdodo-ws]). With
  `Infinity`, a query refetches only when it is invalidated explicitly ([tq-defaults]).
- **`invalidateQueries`** marks the matching queries stale and refetches the ones currently rendered
  ([tq-invalidation]). `refetchType` defaults to `active`; with `none`, it returns without refetching ([tq-client],
  L476–514).
- **`cancelRefetch`** defaults to `true`, which cancels a running fetch before starting the new one ([tq-client],
  L516–560). Without it, `fetch` hands back the promise of the fetch already in flight. That is how concurrent
  fetches of one query are deduplicated ([tq-query], L684–722).
- Refetches on focus and on reconnect pass `cancelRefetch: false` ([tq-query], L578–605). Focus refetching is on by
  default and uses `visibilitychange` ([tq-focus]).
- A successful fetch resets `isInvalidated` to `false` ([tq-query], L1054–1061).
- **Polling.** `refetchInterval` runs one timer per observer and pauses in the background unless
  `refetchIntervalInBackground` is set. Concurrent fetches are deduplicated at the query level ([tq-polling]).
- **Races with optimistic updates.** A refetch already in flight can overwrite a newer optimistic update. TkDodo's fix
  is to call `cancelQueries` in `onMutate`, and in `onSettled` to invalidate only when `isMutating() === 1`
  ([tkdodo-concurrent]).
- **Inference, for `onStale`:**
  - Keep `cancelRefetch` at its default for hint-driven invalidation. Suppose a focus-triggered fetch began before
    the commit and the hint arrives while it runs. With `cancelRefetch: false`, the hint would reuse that fetch, and
    its success would clear the stale flag, so the hint is lost.
  - Give watched queries a high `staleTime`. Otherwise, when a hidden tab becomes visible, the held hint and
    TanStack's own focus refetch both fire, and the hint cancels the focus fetch.

## 4. Load shaping

### 4.1 Thundering herds and jitter

- In AWS's simulation, capped exponential backoff without jitter still left "clusters of calls". Full jitter (sleep a
  random time between zero and the cap) did the least work, and decorrelated jitter finished slightly sooner. The post
  concludes that jittered backoff should be standard for remote clients ([aws-jitter]).
- The Amazon Builders' Library applies jitter to all timers, periodic jobs and delayed work, not only to retries. For
  periodic work it recommends a jitter that stays the same on each host, so that load patterns can be reproduced
  while debugging ([aws-builders]).
- SSE clients wait the server-set reconnection time (the `retry` field) and may wait longer, with exponential backoff
  after a failed attempt. A response that is not a 200 `text/event-stream` fails the connection for good
  ([whatwg-sse]).

### 4.2 Debounce, throttle and the windows real systems use

- A debounce fires on the trailing edge by default, can also fire on the leading edge, and `maxWait` bounds how long
  a call can be delayed. With both edges enabled, it fires on the trailing edge only if it was called more than once
  during the wait ([lodash-docs]). In lodash, `throttle` is a debounce with `maxWait` equal to the wait and both edges
  on ([lodash-src], L10966–10980).

| System | Shape | Window |
| --- | --- | --- |
| Hasura | fixed poll per multiplexed batch | 1 s ([hasura-perf]) |
| PostGraphile v4 | throttle per subscription, leading and trailing | 500 ms ([postgraphile-live]) |
| Turbo server | trailing debounce per (thread, stream, request id) | 0.5 s ([turbo-debouncer]) |
| Turbo client | trailing debounce of page refreshes | 150 ms ([turbo-session]) |
| Meteor polling driver | throttled poll scheduling, one follow-up while running | 50 ms throttle, 10 s idle poll ([meteor-polling]) |
| ADR-0091 draft | leading-edge throttle with trailing hint, per (method, partition), backoff | 1 s base, 30 s cap, 0–250 ms client jitter |

### 4.3 Single-flight and request coalescing

- Go's `singleflight` runs at most one call per key at a time; duplicate callers wait and receive the same result.
  `Forget` makes the next call for the key start a new run instead of joining the running one ([go-singleflight]).
- HybridCache lets only one concurrent caller per key run the factory on a given instance, and that coordination
  does not extend to other instances. Removing by tag is logical: it marks everything created before that moment as
  invalid. Invalidations do not reach the in-memory caches of other servers ([hybridcache]).
- Convex's `Waiting` entries are the same pattern with a snapshot check (section 1.3), and so is the query-level
  fetch deduplication in TanStack (section 3).

## 5. Correctness hazards

### 5.1 A run that is in flight when the data changes

- **The hazard.** A result computed from a snapshot taken before a commit, but finished after the hint for that
  commit, is stale. If it is cached, shared, or marks the client fresh, the hint is lost until the next change.
- **Convex** checks it exactly: a shared or cached result is used only after confirming that no commit since its
  snapshot overlaps its read set ([convex-cache], L770–800). It also keeps the newer of two results ([convex-cache],
  L1059–1086).
- **Meteor** handles it with a follow-up: a write during a poll schedules one more poll ([meteor-polling]).
- **Go `singleflight`** exposes `Forget`, so callers that arrive after an invalidation do not join a run that started
  before it ([go-singleflight]).
- **HybridCache.** Each run gets its creation timestamp when it is created, before the factory runs
  ([hybridcache-src], L33–41). If a tag invalidation happens during the run, the item fails validation and is stamped
  again with the current time before it is stored ([hybridcache-src], L292–318). **Inference, not verified by a
  test:** a value computed before the invalidation can then be stored as valid. The docs do not discuss this case.
- **TanStack**: see section 3. A successful fetch clears `isInvalidated` even if it started before the hint.
- **Inference, the rule for Elarion:** take a generation number before the query takes its snapshot. Any hint that
  arrives after that invalidates the run's result for sharing and schedules one follow-up run. A stored result never
  replaces one with a higher generation.

### 5.2 Ordering and version guards for pushed state

- Convex ties every result to a commit timestamp and keeps all of a client's queries at the same timestamp
  ([convex-how]). Electric addresses data by offset within a shape log and can tell a client to discard its data and
  refetch ([electric-http]). PostgreSQL delivers notifications in commit order but keeps none across a crash or a
  dropped listener ([pg-notify], [pg-async]).
- Elarion's client-event doctrine already asks pushed state to carry a monotonic version, applied through one guard
  that drops anything older ([client-events]). **Inference:** a tier-3 result push needs that version. The
  per-(method, partition) generation from section 5.1 can serve as it.

### 5.3 Consistency inside one dashboard

- Under Read Committed, each statement sees a snapshot taken when that statement starts, so two `SELECT`s in one
  transaction can see different data. Under Repeatable Read, every statement sees the snapshot from the start of the
  first one ([pg-iso]).
- **Inference:** a dashboard handler that runs several statements should read inside a read-only Repeatable Read
  transaction, or its panels can disagree. Separate queries that each re-run on their own hint can also skew against
  each other, which Convex avoids by keeping them at one timestamp ([convex-how]).

### 5.4 Publishing only after commit

- PostgreSQL delivers NOTIFY only on commit ([pg-notify]). Laravel needs `after_commit` or
  `ShouldDispatchAfterCommit` because a queued broadcast could otherwise run before the commit ([laravel-bc-tx]).
  Turbo's model broadcasts are commit callbacks ([turbo-broadcastable]). Meteor marks a write committed only after a
  poll has reflected it ([meteor-polling], [ddp]). ADR-0091 publishes its change set after commit, which follows the
  same baseline.

### 5.5 Stale authorization and visible activity

- Supabase checks authorization at join and caches it until the client sends a new token ([supabase-auth]). SignalR
  groups have no expiry or revocation of their own ([signalr-groups]). **Inference:** pushing state with an
  authorization checked only at subscribe time leaks data after revocation; pushing a hint leaks only when something
  changed, and the re-query enforces current permissions.

### 5.6 The writer's own echo

- Turbo ignores a refresh caused by one of the client's own recent requests ([turbo-session]), and Laravel's
  `toOthers()` skips the sender ([laravel-bc]). **Inference:** without this, the writer's client refetches once for
  its own mutation and once more for the hint.

## 6. Incremental alternatives when one query per change is still too expensive

### 6.1 Incremental view maintenance: `pg_ivm`

- pg_ivm maintains a view immediately: AFTER triggers update it in the same transaction that changes a base table
  ([pg-ivm], L11). It supports inner and outer joins, `DISTINCT`, and the aggregates `count`, `sum`, `avg`, `min` and
  `max`. It does not support `HAVING`, window functions, `ORDER BY`/`LIMIT` and several others ([pg-ivm],
  L404–421).
- To stay correct under concurrency, it takes an `ExclusiveLock` on the view as soon as a base table changes in Read
  Committed, so updates to the view happen one transaction at a time. Under Repeatable Read or Serializable it raises
  an error instead ([pg-ivm], L485–496).
- By its own README, IVM is not effective when base tables are modified often, or when a large part of a table
  changes at once ([pg-ivm], L503).

### 6.2 Materialize

- `SUBSCRIBE` streams a view's changes as diffs with timestamps and progress markers ([materialize-subscribe]).
  Ingesting from PostgreSQL uses the replication protocol with one replication slot per source. While a source is
  disconnected, WAL accumulates behind the slot and cannot be reclaimed ([materialize-pg]). This is the cost that
  ADR-0091 cites when it rejects logical replication.

### 6.3 Summary tables maintained in the writing transaction

- Under Read Committed, a transaction that updates a row another transaction has already updated waits for that
  transaction to commit or roll back. Under Repeatable Read, it fails with a serialization error after the other
  transaction commits ([pg-iso]). A summary row that every write updates therefore serializes all writers.
- PlanetScale's slotted counter spreads the increments over N rows chosen at random and sums them when reading,
  which takes the pressure off the single row ([planetscale-slotted]).
- **Inference:** a summary maintained in the writing transaction combines well with hints. The summary table is the
  declared source, and the re-query reads only the summary. The cost moves to the writers; it does not go away.

### 6.4 When a dashboard should just poll

- Hasura chose interval polling with multiplexing over change events ([hasura-arch]). TanStack's `refetchInterval`
  pauses in the background by default and deduplicates concurrent fetches ([tq-polling]). Meteor keeps poll-and-diff
  as its fallback ([meteor-oplog]). Elarion already recommends polling a keyset query when seconds of staleness are
  acceptable ([client-events]).
- **Inference:** poll when the change rate is always above what hints can usefully deliver, when the screen tolerates
  the polling interval as staleness, or when writes happen outside what the change set sees (raw SQL, triggers,
  other applications).

## 7. Decision guide

### 7.1 Table

"Watchers" means concurrent viewers of one method in one partition on one node. The thresholds are illustrations,
not measurements.

| Situation | Recommended pattern | Why | What it costs |
| --- | --- | --- | --- |
| Few watchers (roughly ≤ 10–20), cheap query, any change rate | **Tier 1: immediate per-viewer re-query** (one in flight, one merged follow-up, no timer) | The load is bounded by watchers × the query rate the query itself allows. Same follow-up rule as Meteor ([meteor-polling]). | Up to one query per watcher per query duration while writes continue. |
| Moderate watchers, cheap or medium query, bursty writes | **Tier 2: throttled per-viewer re-query** (first hint at once, then a minimum interval) | A fixed throttle on both edges, as in PostGraphile, which documents its herd limit ([postgraphile-live]). | Watchers ÷ interval queries per second; latency up to one interval. |
| Many watchers, same projection for the whole partition (tenant or role), expensive query or high change rate | **Tier 3: shared result** (single run per node per key, merged follow-up, served to re-queries or broadcast) | All large-scale systems share work this way ([hasura-exec], [meteor-multiplex], [convex-cache]). | Nodes × one query per change; a generation guard; an authorization key; payload bandwidth if results are broadcast ([azure-signalr-perf]). |
| Many watchers, but the projection is per user (row filters, per-user authorization) | **Tier 2 with a longer interval**, or split the screen into a shared part (tier 3) and a small per-user part | Sharing needs an identical authorization context ([hasura-perf], [convex-cache], [electric-auth]). | Per-viewer load stays. |
| Seconds to minutes of staleness acceptable, or writes are always heavy, or writes bypass the change set | **Poll a keyset query** | Polling has a fixed cost and does not depend on detecting writes ([hasura-arch], [tq-polling]). | One query per watcher per interval even when nothing changed. |
| A detail screen for one resource, a hint that must carry an id or intent, or when the timing of changes is sensitive | **Explicit resource-scoped client events** | Row-level targeting and its own authorization per topic ([client-events]). | A writer-side event and projection per topic. |
| Even one query per change is too expensive (large aggregates), and writes are rare compared to reads | **Incremental summary** (summary table, pg_ivm), then hints on the summary | Moves the work to the write path ([pg-ivm]). | Writers serialize on the summary ([pg-ivm], [pg-iso]); slotted rows mitigate that ([planetscale-slotted]). |
| Every element matters (a feed or a log) | Not hints: ordered streams or integration events | Hints are lossy by design ([client-events]). | See the client-events page. |

### 7.2 Decision tree

1. Does the client need every element, or an id or intent? Use streams, integration events, or resource-scoped client
   events.
2. Are seconds to minutes of staleness acceptable, or do writes bypass the change set? Poll a keyset query.
3. Would one query per change be too expensive even when it is shared? Maintain an incremental summary and watch it.
4. Is the projection identical for everyone in a tenant or role, with many watchers? Use tier 3.
5. Otherwise, with few watchers use tier 1, and with more watchers use tier 2.

### 7.3 Tier 3 as the research suggests it (inference)

- **Opt in per handler.** The handler declares that its result depends only on its arguments, the tenant, and a
  bounded authorization key such as a role set. This is the equivalent of Convex leaving out identity when a query
  never read auth ([convex-cache]). Without the declaration, nothing is shared.
- **Per node and key** (method, canonical arguments, partition, authorization key):
  - A hint for the key's sources increments a dirty generation.
  - One run at a time takes the generation before opening its snapshot.
  - A result is stored only if its generation is higher than the stored one ([convex-cache]).
  - If the dirty generation moved during the run, one follow-up runs, waiting at least as long as the last run took
    (ADR-0091's spacing rule; [meteor-polling]).
- **Serving re-queries (variant A).** Clients still get hints and still re-query through the full pipeline,
  authorization included. A re-query whose generation is current reads the stored result, or joins a run that started
  after the latest hint. This is request collapsing inside the server ([electric-http], [go-singleflight]), and it
  changes nothing on the wire.
- **Broadcasting the result (variant B).** The node pushes the result with its generation as the version, and the
  client applies it through one stale-drop guard ([client-events]). NOTIFY cannot carry results ([pg-notify]), so each
  node computes for its own watchers, as Meteor does ([meteor-multiplex]). Variant B costs payload × watchers
  ([azure-signalr-perf]) and has the revocation weakness from section 5.5.
- **Do not build on HybridCache alone.** Its stampede protection is per instance, its invalidations do not reach
  other servers' memory, and the possible stale store from section 5.1 remains ([hybridcache], [hybridcache-src]).
  ADR-0004's cache scopes are `CurrentUser` and `Global` ([adr-0004]), so a tenant scope would be new.

## 8. Where the research refines or contradicts the ADR-0091 draft

1. **1 s base interval: consistent.** It matches Hasura's 1 s default ([hasura-perf]) and is in the same range as
   PostGraphile's 500 ms ([postgraphile-live]) and Turbo's 0.5 s plus 150 ms ([turbo-debouncer], [turbo-session]).
   The leading edge plus a guaranteed trailing hint is lodash's throttle with both edges on ([lodash-src]).
2. **30 s backoff cap: no precedent, and it penalizes small audiences.** No surveyed system lowers freshness because
   the change rate rises. They bound cost by sharing or with a fixed throttle (sections 1.1–1.6). A tenant with steady
   writes and two watchers would sit at 30 s staleness even though its load is trivial.
   - Refinement: tie backoff to the cost of the audience, for example engaging only above a watcher threshold for a
     (method, partition), or let tier 3 take over at that point.
   - If backoff stays, measure a smaller cap (5–10 s) in the planned benchmark.
   - Because every node backs off in lockstep and the trailing hints fall due together, the end of each interval is
     a synchronized herd. Jitter has to cover that (item 3).
3. **250 ms jitter: too narrow for large herds.** AWS shows that only jitter over the full window removes clusters
   ([aws-jitter]).
   - Refinement: scale the jitter with the current interval, for example uniform over [0, interval) with an upper
     bound. A deterministic offset per connection keeps load patterns reproducible ([aws-builders]).
   - Apply the same jitter to the `onStale` burst on `elarion.connected` after a listener reconnect. That burst
     re-queries every watched method of every subscriber on the node at once (ADR §2, [client-events]).
   - Consider spreading SSE reconnects through the `retry` field ([whatwg-sse]).
4. **100 ms broadcast window: consistent with the PostgreSQL source.** The NOTIFY commit lock is real and serializes
   every NOTIFY commit ([pg-async]). Two refinements:
   - Split a publication that exceeds 8000 bytes into several NOTIFYs before collapsing it to the broadcast
     partition, because the collapse turns a change in one tenant into a re-query herd across all tenants
     ([pg-notify]).
   - Keep the LISTEN connection out of transactions and monitor `pg_notification_queue_usage`, because a long
     listener transaction blocks queue cleanup and a full queue fails commits ([pg-notify]).
5. **Hint rather than state: confirmed as the default.** Sources: TkDodo ([tkdodo-ws]), Supabase's authorization
   cached per connection ([supabase-auth]), Phoenix's per-socket encoding cost ([phoenix-channel]), and NOTIFY's
   payload limit ([pg-notify]). Refinement: allow state only for tier 3 variant B, where the partition is the
   authorization boundary and the result carries a version (section 7.3).
6. **Tenant partitioning: consistent.** It matches Supabase's private-channel topics ([supabase-subscribing]) and
   Hasura's cohorts by session variables ([hasura-perf]). Refinements:
   - Writes in system scope fan out to every partition, so they should name tenants through `IRequeryHints` when
     they can, and broadcast-partition hints deserve the wider jitter from item 3.
   - Re-authorize subscriptions when the session renews, as Supabase does on a new token ([supabase-auth]). SignalR
     notes that groups have no revocation of their own ([signalr-groups]).
7. **In-flight rule: confirmed.** Meteor's follow-up poll ([meteor-polling]) and Convex's snapshot check
   ([convex-cache]) agree with it. Refinements for the TanStack wiring:
   - Document that the `onStale` example must keep the default `cancelRefetch: true`, because a successful fetch that
     started before the commit clears the stale flag ([tq-query], [tq-client]).
   - Recommend a high `staleTime` for watched queries, so focus and mount do not refetch alongside the hints
     ([tkdodo-ws], [tq-defaults]).
8. **Hidden-tab hold: consistent.** It mirrors TanStack pausing polling in the background and refetching on focus
   ([tq-polling], [tq-focus]). With a high `staleTime` (item 7), the held hint is the only refetch.
9. **Shared cache in §7 (via ADR-0004): needs more than invalidation.** HybridCache coordinates per instance, does
   not invalidate other servers' memory caches, and may store a result that overlapped an invalidation
   ([hybridcache], [hybridcache-src]). Tier 3 needs a per-node generation guard driven by the same publication
   (section 7.3).
10. **Weaker delivery: consistent, and the safety net works as described.** PostgreSQL notifications do not survive a
    crash ([pg-async]). A `refetchInterval` safety net runs per observer and is deduplicated per query
    ([tq-polling]).
11. **Writer's echo: worth an option.** Turbo and Laravel skip the originator ([turbo-session], [laravel-bc]). In
    ADR-0091, publications from several transactions are merged, so an origin id survives only when the merged hint
    comes entirely from one client's writes. This is listed under Open questions.
12. **Consistency within a dashboard: worth a sentence in the ADR.** A handler with several statements should read in
    a read-only Repeatable Read transaction ([pg-iso]). Separately re-queried panels can skew, which Convex prevents
    with a shared timestamp ([convex-how]).

## 9. Open questions

- **HybridCache stale store.** From the source (section 5.1), a factory run that overlaps `RemoveByTagAsync` appears
  to be stored as valid. This needs a test before ADR-0091 or ADR-0004 rely on tag invalidation for shared results.
- **Convex cross-user sharing** is documented only in the source code, not in the docs. Whether the backend still
  behaves this way should be checked against a release, not only `main`.
- **Supabase `realtime.send` / `broadcast_changes` commit semantics.** That only committed rows are broadcast is an
  inference from WAL-based delivery ([supabase-broadcast]); the docs do not say it.
- **Electric cache headers.** The page names `max-age`, `stale-age` and `etag` but gives no values for live requests
  ([electric-http]). I did not check the sync service source.
- **Turbo dropped refreshes.** I did not find whether a refresh dropped during a navigation is ever retried
  ([turbo-session]).
- **Adaptive backoff.** None of the surveyed systems documents backing off freshness under load, so the 30 s cap and
  any audience threshold need the benchmark the ADR already plans.
- **PostGraphile v5.** Only the v4 live-query docs were checked.
- **Phoenix compute-once guidance.** No official LiveView document prescribes computing in a publisher versus per
  socket.
- **Originator suppression in merged publications** (section 8, item 11). This is a design question, not a research
  one.

## Sources

Code links are pinned to the commit read on 2026-10-25. The electric-sql.com, graphile.org and hexdocs.pm
addresses redirected to the vendors' current domains, which are listed below.

[adr-0004]: ../0004-handler-result-caching.md
[client-events]: ../../capabilities/events/client-events.mdx
[hasura-livequery]: https://hasura.io/docs/latest/subscriptions/postgres/livequery/index/
[hasura-exec]: https://hasura.io/docs/2.0/subscriptions/postgres/livequery/execution/
[hasura-perf]: https://hasura.io/docs/2.0/subscriptions/observability-and-performance/
[hasura-streaming]: https://hasura.io/docs/2.0/subscriptions/postgres/streaming/index/
[hasura-arch]: https://github.com/hasura/graphql-engine/blob/aeb256c9666d89c341f7863f00b4bc241cac4d22/architecture/live-queries.md
[meteor-multiplex]: https://github.com/meteor/meteor/blob/5e845e446c4ae91193b883144408859a2720db9c/packages/mongo/observe_multiplex.ts
[meteor-conn]: https://github.com/meteor/meteor/blob/5e845e446c4ae91193b883144408859a2720db9c/packages/mongo/mongo_connection.js
[meteor-polling]: https://github.com/meteor/meteor/blob/5e845e446c4ae91193b883144408859a2720db9c/packages/mongo/polling_observe_driver.ts
[meteor-oplog]: https://github.com/meteor/docs/blob/master/long-form/oplog-observe-driver.md
[meteor-api]: https://docs.meteor.com/api/meteor.html
[ddp]: https://github.com/meteor/meteor/blob/devel/packages/ddp/DDP.md
[convex-how]: https://stack.convex.dev/how-convex-works
[convex-realtime]: https://docs.convex.dev/realtime
[convex-query]: https://docs.convex.dev/functions/query-functions
[convex-cache]: https://github.com/get-convex/convex-backend/blob/bb31399c8704ceb6c19003936f6e144375b2c3bc/crates/application/src/cache/mod.rs
[supabase-pgchanges]: https://supabase.com/docs/guides/realtime/postgres-changes
[supabase-subscribing]: https://supabase.com/docs/guides/realtime/subscribing-to-database-changes
[supabase-broadcast]: https://supabase.com/docs/guides/realtime/broadcast
[supabase-auth]: https://supabase.com/docs/guides/realtime/authorization
[electric-shapes]: https://electric.ax/docs/guides/shapes
[electric-http]: https://electric.ax/docs/api/http
[electric-auth]: https://electric.ax/docs/guides/auth
[postgraphile-live]: https://postgraphile.org/postgraphile/4/live-queries/
[signalr-hubcontext]: https://learn.microsoft.com/en-us/aspnet/core/signalr/hubcontext
[signalr-groups]: https://learn.microsoft.com/en-us/aspnet/core/signalr/groups
[signalr-scale]: https://learn.microsoft.com/en-us/aspnet/core/signalr/scale
[signalr-redis]: https://learn.microsoft.com/en-us/aspnet/core/signalr/redis-backplane
[signalr2-scaleout]: https://learn.microsoft.com/en-us/aspnet/signalr/overview/performance/scaleout-in-signalr
[signalr-security]: https://learn.microsoft.com/en-us/aspnet/core/signalr/security
[azure-signalr-perf]: https://learn.microsoft.com/en-us/azure/azure-signalr/signalr-concept-performance
[turbo-handbook]: https://turbo.hotwired.dev/handbook/page_refreshes
[turbo-broadcastable]: https://github.com/hotwired/turbo-rails/blob/37530c08780fa6f6dbb56a633de4b81169bdd174/app/models/concerns/turbo/broadcastable.rb
[turbo-broadcasts]: https://github.com/hotwired/turbo-rails/blob/37530c08780fa6f6dbb56a633de4b81169bdd174/app/channels/turbo/streams/broadcasts.rb
[turbo-debouncer]: https://github.com/hotwired/turbo-rails/blob/37530c08780fa6f6dbb56a633de4b81169bdd174/app/models/turbo/debouncer.rb
[turbo-threaddebouncer]: https://github.com/hotwired/turbo-rails/blob/37530c08780fa6f6dbb56a633de4b81169bdd174/app/models/turbo/thread_debouncer.rb
[turbo-session]: https://github.com/hotwired/turbo/blob/75350a19212ef0d29a0e6db75e0bfdeb96b10cd7/src/core/session.js
[laravel-bc]: https://laravel.com/docs/12.x/broadcasting
[laravel-bc-tx]: https://laravel.com/docs/12.x/broadcasting#broadcasting-and-database-transactions
[phoenix-pubsub]: https://phoenix-pubsub.hexdocs.pm/Phoenix.PubSub.html
[phoenix-channel]: https://phoenix.hexdocs.pm/Phoenix.Channel.html
[liveview]: https://phoenix-live-view.hexdocs.pm/Phoenix.LiveView.html
[pg-notify]: https://www.postgresql.org/docs/current/sql-notify.html
[pg-async]: https://github.com/postgres/postgres/blob/2ebd684974375e9c68b43577b7e46a83597c8d95/src/backend/commands/async.c
[pg-iso]: https://www.postgresql.org/docs/current/transaction-iso.html
[tkdodo-ws]: https://tkdodo.eu/blog/using-web-sockets-with-react-query
[tkdodo-concurrent]: https://tkdodo.eu/blog/concurrent-optimistic-updates-in-react-query
[tq-invalidation]: https://github.com/TanStack/query/blob/659431a5f5cc6be62219c3632b7aa4d44ec1fc18/docs/framework/react/guides/query-invalidation.md
[tq-defaults]: https://github.com/TanStack/query/blob/659431a5f5cc6be62219c3632b7aa4d44ec1fc18/docs/framework/react/guides/important-defaults.md
[tq-focus]: https://github.com/TanStack/query/blob/659431a5f5cc6be62219c3632b7aa4d44ec1fc18/docs/framework/react/guides/window-focus-refetching.md
[tq-polling]: https://github.com/TanStack/query/blob/659431a5f5cc6be62219c3632b7aa4d44ec1fc18/docs/framework/react/guides/polling.md
[tq-client]: https://github.com/TanStack/query/blob/659431a5f5cc6be62219c3632b7aa4d44ec1fc18/packages/query-core/src/queryClient.ts
[tq-query]: https://github.com/TanStack/query/blob/659431a5f5cc6be62219c3632b7aa4d44ec1fc18/packages/query-core/src/query.ts
[aws-jitter]: https://aws.amazon.com/blogs/architecture/exponential-backoff-and-jitter/
[aws-builders]: https://aws.amazon.com/builders-library/timeouts-retries-and-backoff-with-jitter/
[whatwg-sse]: https://html.spec.whatwg.org/multipage/server-sent-events.html
[lodash-docs]: https://lodash.com/docs/4.17.15#debounce
[lodash-src]: https://github.com/lodash/lodash/blob/4.17.21/lodash.js
[go-singleflight]: https://pkg.go.dev/golang.org/x/sync/singleflight
[hybridcache]: https://learn.microsoft.com/en-us/aspnet/core/performance/caching/hybrid
[hybridcache-src]: https://github.com/dotnet/extensions/blob/2c49190ec3c791e61a197e395ed06c52dc6c8170/src/Libraries/Microsoft.Extensions.Caching.Hybrid/Internal/DefaultHybridCache.StampedeStateT.cs
[pg-ivm]: https://github.com/sraoss/pg_ivm/blob/678c37861080aa221b859cefaca98725646dbf7c/README.md
[materialize-subscribe]: https://materialize.com/docs/sql/subscribe/
[materialize-pg]: https://materialize.com/docs/ingest-data/postgres/
[planetscale-slotted]: https://planetscale.com/blog/the-slotted-counter-pattern

| Label | Source |
| --- | --- |
| hasura-livequery, hasura-exec, hasura-perf, hasura-streaming | Hasura docs: live queries, execution, observability and performance tuning, streaming subscriptions |
| hasura-arch | Hasura graphql-engine repository, `architecture/live-queries.md` |
| meteor-multiplex, meteor-conn, meteor-polling | Meteor source: `observe_multiplex.ts`, `mongo_connection.js`, `polling_observe_driver.ts` |
| meteor-oplog, meteor-api, ddp | Meteor docs: oplog observe driver, publication strategies, DDP specification |
| convex-how, convex-realtime, convex-query | Convex Stack "How Convex Works"; Convex docs: realtime, query functions |
| convex-cache | Convex backend source: `crates/application/src/cache/mod.rs` |
| supabase-pgchanges, supabase-subscribing, supabase-broadcast, supabase-auth | Supabase Realtime docs |
| electric-shapes, electric-http, electric-auth | Electric docs: shapes, HTTP API, auth |
| postgraphile-live | PostGraphile v4 docs: live queries |
| signalr-hubcontext, signalr-groups, signalr-scale, signalr-redis, signalr-security | ASP.NET Core SignalR docs |
| signalr2-scaleout | ASP.NET SignalR 2 docs: introduction to scale-out |
| azure-signalr-perf | Azure SignalR Service performance guide |
| turbo-handbook | Turbo handbook: page refreshes |
| turbo-broadcastable, turbo-broadcasts, turbo-debouncer, turbo-threaddebouncer | turbo-rails source |
| turbo-session | Turbo source: `src/core/session.js` |
| laravel-bc, laravel-bc-tx | Laravel 12.x docs: broadcasting |
| phoenix-pubsub, phoenix-channel, liveview | Phoenix.PubSub, Phoenix.Channel and Phoenix.LiveView docs |
| pg-notify, pg-iso | PostgreSQL docs: NOTIFY, transaction isolation |
| pg-async | PostgreSQL source: `src/backend/commands/async.c` |
| tkdodo-ws, tkdodo-concurrent | TkDodo's blog (TanStack Query maintainer) |
| tq-invalidation, tq-defaults, tq-focus, tq-polling | TanStack Query docs (repository) |
| tq-client, tq-query | TanStack Query source: `queryClient.ts`, `query.ts` |
| aws-jitter | AWS Architecture Blog: "Exponential Backoff and Jitter" |
| aws-builders | Amazon Builders' Library: "Timeouts, retries, and backoff with jitter" (redirects to builder.aws.com) |
| whatwg-sse | WHATWG HTML Living Standard: server-sent events |
| lodash-docs, lodash-src | lodash 4 docs and source |
| go-singleflight | Go `golang.org/x/sync/singleflight` package docs |
| hybridcache, hybridcache-src | ASP.NET Core HybridCache docs; dotnet/extensions source `DefaultHybridCache.StampedeStateT.cs` |
| pg-ivm | pg_ivm README |
| materialize-subscribe, materialize-pg | Materialize docs: SUBSCRIBE, PostgreSQL ingestion |
| planetscale-slotted | PlanetScale blog: "The slotted counter pattern" |
| adr-0004, client-events | Elarion ADR-0004 and the client-events capability page |
