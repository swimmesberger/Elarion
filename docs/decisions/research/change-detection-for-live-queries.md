# Change detection for live queries: where frameworks notice that data changed

- Date: 2026-10-25
- Status: Research input for section 2 ("Producing publications") of
  [ADR-0091](../0091-query-sources-and-requery-hints.md). This is not a decision.
- Scope: the layer at which a write is detected, what that layer misses, and when it publishes. Delivery, fan-out,
  throttling and the browser side are in [live-dashboards-and-fan-out.md](live-dashboards-and-fan-out.md) and are not
  repeated; "fan-out §n" points to a section of that note.
- Method: primary sources only: vendor docs, source code pinned to a commit, release notes, and posts by vendors or
  maintainers. Labels resolve in [Sources](#sources). **Inference** marks my reading. Quotes are short phrases.

## Summary

1. **ORM and model hooks are the mainstream layer.** Rails, Laravel, Django, Hibernate and ABP detect writes in the
   ORM (commit callbacks, model events, signals, entity listeners, a `SaveChanges` override). An ORM hook is common
   practice, not an outlier.
2. **Each documents that set-based statements bypass its hooks, and none intercepts them:** `update_all`, Eloquent
   mass updates, `QuerySet.update()`, bulk HQL; ABP says features "won't work" with `DeleteDirectAsync`. ADR-0091's
   `ExecuteUpdate` tag at query compilation has no precedent in the survey, and it is the part that leans on EF Core
   internals.
3. **Elixir moved detection out of the persistence layer on purpose.** Ecto deprecated model callbacks in 1.1 (hidden
   flow, side effects inside transactions that may roll back), and `Ecto.Repo` has no after-commit hook. Phoenix's
   generated contexts broadcast explicitly after each `Repo` call.
4. **Ash notifiers are the closest analogue to a command-boundary producer:** declared on resources and actions, sent
   after commit, at most once. Bulk actions send none unless `notify?: true`, and Ash warns about "missed
   notifications" when an action runs inside a transaction it did not start.
5. **After commit is the norm, not every default.** Rails `after_commit`, Django `on_commit`, Spring `AFTER_COMMIT`,
   Hibernate post-commit listeners and Ash publish after commit. Laravel model broadcasts default to
   `broadcastAfterCommit() = false`; Django `post_save` and ABP local entity events run inside the transaction.
6. **The docs push toward explicit code.** Django warns that signals make code hard to follow and recommends direct
   calls. EF Core assigns interceptors to "manipulating" operations and events to "reacting", and suggests changing
   the query directly before writing an expression interceptor.
7. **Only database-level mechanisms see every writer.** Triggers fire for foreign-key cascades on the referencing
   table; logical replication (Supabase, Electric, Zero, Debezium, Prisma Pulse) sees everything, at the price of
   `wal_level = logical` and a slot that retains WAL.
8. **Database-level detection has not always paid off.** Hasura rejected triggers and WAL for live queries and polls
   (fan-out §1.1). Prisma built Pulse on CDC and removed its docs in March 2025 as "outdated content".
9. **Triggers that insert into a log table, which a worker polls, avoid NOTIFY's commit lock** (Hasura event triggers,
   Supabase broadcast-from-database) and are transactional by construction. Polling a sequence-keyed log needs gap
   handling, which Marten documents.
10. **Gleam has no data-change mechanism.** Lustre server components broadcast explicitly through process registries;
    `pog` has transactions but no LISTEN or replication API.
11. **Hybrids exist.** Meteor pairs in-process write notifications with a 10 s poll; Redwood and Next.js declare the
    read side and invalidate explicitly; Convex owns read and write sets because it is the database.
12. **Inference:** ADR-0091's hints are type-level, so the producer only answers "which tables did this transaction
    write". A change-tracker scan, a call-site report or a statement-level trigger suffices; no row images are needed.

## Comparison

| System | Layer that detects | Misses | Publishes | Maps to subscribers by |
| --- | --- | --- | --- | --- |
| Phoenix generated contexts | explicit code in context functions | `update_all`, `delete_all`, raw SQL, other writers, cascades | after the `Repo` call; before commit inside an outer transaction (inference) | topic `scope:key:plural`, `{:created, struct}` |
| Ecto | none (callbacks removed); `prepare_query` sees query APIs only | n/a | n/a: code placed after `Repo.transact` | n/a |
| Ash notifiers | declared resource actions | bulk actions by default, non-Ash writes (inference) | after commit, at most once | PubSub topic templates from record fields, old and new values |
| Commanded projections | the domain event | non-event writes | after the projection transaction | application PubSub |
| Lustre (Gleam) | explicit messages | anything not sent | when sent | process-group topic |
| Rails + Turbo | model commit callbacks | `update_all`, `delete_all`, `insert_all`, `upsert_all`, `update_column(s)`, raw SQL | after commit | stream per model or association; `touch: true` to the parent |
| Laravel | model events, `BroadcastsEvents` | mass update and delete, `saveQuietly`, raw queries | in the transaction by default; after commit opt-in | `broadcastOn` channels, model-name conventions |
| Django | signals; explicit `on_commit` | `update()`, `bulk_create`, `bulk_update`, raw SQL | `post_save` in the transaction; `on_commit` after it | application code (Channels groups) |
| Hibernate | entity listeners | bulk HQL and Criteria mutations, native SQL (inference) | `PostCommit*` after commit; others before | application |
| Spring / Spring Data | application events; `@DomainEvents` on `save`/`delete` | anything unpublished; `deleteById`, query methods | listener phase, default `AFTER_COMMIT` | event type |
| ABP | its `SaveChanges` override, recorded on the unit of work | `DeleteDirectAsync`; `ExecuteUpdate`, raw SQL (inference) | local: before commit; distributed: end of unit of work or outbox | event type per entity |
| EFCore.Triggered | `SaveChanges` interceptor | set-based and raw writes (inference) | around save; after commit only if the app raises it | trigger per entity type |
| Marten | the event-store sequence | non-event writes | async daemon, by high-water mark | projections |
| Prisma extensions / Pulse | query extension / CDC | extensions: raw-SQL tables, cascades, other clients | at call / from WAL | model streams |
| Supabase, Electric, Zero, Debezium | logical replication | nothing on the database | after commit | subscriber filters, shapes, topics |
| PostGraphile | user-written triggers + NOTIFY; v4 live: logical decoding | tables without a trigger | at commit | topics |
| Hasura | polling (live queries); triggers into a log table (event triggers) | nothing, at polling cost | interval | multiplexed cohorts |
| Meteor | in-process write notifications + oplog or poll | other processes, up to 10 s, in poll mode | after the write | per cursor |
| Convex | the database's read and write sets | nothing (sole writer) | per commit | read-set overlap |
| Redwood, Next.js | explicit invalidation | anything not invalidated | when called | resource ids, schema coordinates, tags |
| ADR-0091 draft | EF interceptors + tag + hand reports | unreported raw SQL and SQL tier, triggers, other apps, migrations | after the outermost commit | type → watching methods |

## 1. Elixir

### 1.1 Phoenix: broadcast from the context function

- The scoped context generator emits `subscribe_<plural>(scope)` and a private `broadcast_<singular>(scope, message)`
  on the topic `"<scope>:<key>:<plural>"` ([phx-gen-scope], L15–25). `create_*`, `update_*` and `delete_*` call
  `Repo.insert/update/delete` and broadcast `{:created | :updated | :deleted, struct}` only on `{:ok, _}`
  ([phx-gen-scope], L70–78, L92–102, L116–124). LiveView subscribes in `mount` and matches them in `handle_info`
  ([phx-scopes]).
- **Inference from the template:** writes that bypass the context function (`update_all`, `delete_all`, raw SQL, other
  applications, database cascades) are not broadcast. Inside an outer `Repo.transact` the broadcast precedes the
  commit, since neither the template nor Ecto (§1.2) has a commit hook.
- Elixir's PostgreSQL driver offers LISTEN through `Postgrex.Notifications`; notifications sent during a disconnect
  "cannot be recovered", so the docs advise subscribe, then fetch, then process ([postgrex-notif]).

### 1.2 Ecto: callbacks removed, no after-commit hook

- Ecto 1.1 deprecated `Ecto.Model` callbacks among features "confusing, unclear or error prone"
  ([ecto-changelog-1.1], L7–9); 2.0 removed them. José Valim's announcement gives the reasons ([ecto-1.1-post]):
  `after_insert`/`after_update` run inside a transaction that can still roll back, so side effects such as indexing
  leak; the flow is hidden; callbacks apply to every operation and do not compose. Changesets and `Ecto.Multi` replace
  them.
- Today's `Ecto.Repo` user callbacks are `init`, `default_options`, `prepare_query` and `prepare_transaction`
  ([ecto-repo], L1365–1446). `prepare_query` runs for query APIs (`:all`, `:update_all`, `:delete_all`, `:stream`,
  `:insert_all` with a source query) and not for the schema functions; `prepare_transaction` runs before a transaction.
  There is no commit callback. Telemetry events carry each statement's `:source` ([ecto-repo], L150–216).
- **Inference:** Ecto's "after commit" is code placement: act after `Repo.transact` returns `{:ok, _}`.

### 1.3 Ash: notifiers on declared actions

- Notifiers are declared on a resource or on one action ([ash-action-opts], L92–95). `Ash.Notifier.PubSub` declares
  `publish`/`publish_all` per action with topic templates; `:_pkey` and `:_tenant` are special, and a changed templated
  field publishes to both the old and the new topic ([ash-pubsub], [ash-pubsub-src], L25–60).
- Notifiers are "called after the current transaction is committed"; notifications accumulate and are sent together;
  they suit at-most-once effects, and signals, Oban or Reactor serve effects that must happen ([ash-notifiers]).
- **Foreign transactions.** `return_notifications?: true` hands notifications back for `Ash.Notifier.notify/1` after
  commit ([ash-notifiers]). Produced but unsent notifications log "Missed N notifications", or raise if configured
  ([ash-helpers], L634–690). `Ash.transaction/3` sends queued notifications only when it started the outermost
  transaction ([ash-src], L4618–4675).
- **Bulk actions.** `notify?` defaults to `false`; enabling it makes the data layer return each batch's records,
  "intensive for large bulk actions" ([ash-src], L541–549, L4046–4048).
- **Inference:** writes outside Ash actions (direct Ecto, SQL, other applications) and database cascades are unseen.

### 1.4 Commanded projections

- A projector's `after_update/3` runs after `Repo.transaction(multi)` returns `{:ok, changes}`, so after the projection
  commits ([cep-src], L97–99); the guide suggests it for PubSub notification, and `projection_versions` projects each
  event once ([cep-usage]). **Inference:** the event is the change signal, complete for what is expressed as events.

## 2. Gleam

There is little. A Lustre server component is an OTP actor that receives external updates as messages, and
`server_component.select` subscribes it to a pub-sub ([lustre-sc]). The official pub-sub example joins a
`group_registry` topic and broadcasts each user action explicitly ([lustre-pubsub], L61–82, L141–158). `pog` offers
`transaction` (commit on `Ok`, rollback on `Error` or panic) and no LISTEN or replication API ([pog-src], L420–430;
[pog-docs]). `squirrel` generates typed functions from `.sql` files by asking PostgreSQL about each query ([squirrel]).
**Inference:** the pattern is Phoenix's without a generator. SQL-first codegen knows every statement at build time,
which would allow static write-set extraction, but no tool does it.

## 3. Ruby on Rails and Turbo

- `after_commit`/`after_*_commit` serve "external systems": they run outside the transaction, an exception rolls
  nothing back and skips the remaining callbacks, and they fire once per record per transaction ([rails-callbacks]).
- Callbacks are skipped by `update_all`, `delete_all`, `delete_by`, `insert_all`, `upsert_all`, `update_column(s)`,
  `touch_all` and the counter methods ([rails-callbacks]); `delete_all` also ignores `:dependent` ([rails-relation]).
- Rails 7.2 added `transaction.after_commit`, `current_transaction` and `ActiveRecord.after_all_transactions_commit`
  for code inside or outside a transaction, and defers Active Job enqueueing to commit ([rails-7.2]).
- Turbo's `broadcasts_to`, `broadcasts`, `broadcasts_refreshes` and `broadcasts_refreshes_to` register commit
  callbacks (`after_create_commit`, `after_update_commit`, `after_destroy_commit`, `after_commit`)
  ([turbo-broadcastable], L183–219). A child reaches its parent's stream through `belongs_to :board, touch: true`
  ([turbo-broadcastable], L100–104).
- **Inference:** a database `ON DELETE CASCADE` is invisible; only `dependent: :destroy` runs callbacks.

## 4. Laravel

- Mass updates and deletes fire no `saved`, `updated`, `deleting` or `deleted`, "because the models are never actually
  retrieved"; observers run at once unless they implement `ShouldHandleEventsAfterCommit` ([laravel-events]).
- `BroadcastsEvents` broadcasts `created`, `updated`, `deleted`, `trashed` and `restored` to `broadcastOn($event)`
  channels under names such as `PostUpdated` ([laravel-model-bc]). It hooks model events ([laravel-bc-src], L14–36)
  and copies `broadcastAfterCommit()`, `false` by default, into the queued event ([laravel-bc-src], L129–144,
  L188–196). The docs warn a broadcast queued in a transaction may run before commit; the fix is the queue's
  `after_commit` option or `ShouldDispatchAfterCommit` ([laravel-bc-tx]).
- **Inference:** by default a model broadcast can reach a client before the row is visible.

## 5. Django and Channels

- Signals give "the appearance of loose coupling"; the docs recommend calling the handling code directly where
  possible ([django-signals]).
- `update()`, `bulk_create` and `bulk_update` call no `save()` and send no `pre_save`/`post_save`; `delete()` sends
  `pre_delete`/`post_delete` for every deleted object, including cascaded deletions ([django-qs]).
- `transaction.on_commit` runs after the outermost commit, is discarded on rollback (including to a savepoint), runs at
  once in autocommit, and suits emails and cache invalidation; there is deliberately no rollback hook ([django-tx]).
- Channels sends to groups through `group_send` from anywhere and has no ORM integration ([channels-layers]).

## 6. JVM: Hibernate, Spring, Spring Data, Spring Modulith

- Hibernate offers Jakarta Persistence callbacks, `Interceptor` and native listeners; a post-insert notification "does
  not establish that the transaction committed" ([hib-events], L24). `PostCommitInsertEventListener` and its siblings
  are "called after an entity insert is committed" ([hib-postcommit]).
- Bulk HQL and Criteria mutations invoke no lifecycle callbacks or per-entity cascades ([hib-query], L405), and a bulk
  `DELETE` bypasses `REMOVE` cascading and orphan removal ([jpa-spec], L394–416).
- `@TransactionalEventListener` runs `BEFORE_COMMIT`, `AFTER_COMMIT` (default), `AFTER_ROLLBACK` or
  `AFTER_COMPLETION`, and is skipped without a transaction unless `fallbackExecution` is set ([spring-tx-events]).
- Spring Data publishes `@DomainEvents` from `save`, `saveAll`, `delete`, `deleteAll` and the batch deletes, not from
  `deleteById`, which may delete without loading the aggregate ([spring-data-events]). **Inference:** derived and
  `@Modifying` queries publish nothing.
- Spring Modulith publishes from the application service; `@ApplicationModuleListener` is `@Async` +
  `@Transactional(REQUIRES_NEW)` + `@TransactionalEventListener`, and its registry stores one entry per listener in the
  business transaction ([modulith-events]).

## 7. .NET

### 7.1 EF Core's own guidance

- EF lists events for "Reacting to EF events" and interceptors for "Manipulating EF operations" ([ef-overview]).
  Events are synchronous, per context, and cover `SavingChanges`, `SavedChanges`, `SaveChangesFailed`,
  `ChangeTracker.Tracked` and `StateChanged`; none covers transactions or `ExecuteUpdate` ([ef-events]).
- Transaction, command and `SaveChanges` interceptors are per context. `IQueryExpressionInterceptor` is a singleton,
  and the docs warn expression-tree interception is easy to get wrong and suggest changing the query directly
  ([ef-interceptors]).
- `ExecuteUpdate`/`ExecuteDelete` are "completely unaware" of the change tracker and start no implicit transaction
  ([ef-execute]). Experimental precompiled queries are emitted at publish as C# interceptors ([ef-aot]); ADR-0091 notes
  they never run `QueryCompilationStarting`.

### 7.2 ABP, EFCore.Triggered, Marten

- **ABP.** Local entity events fire "just after" a change but before the transaction completes, at `SaveChanges`;
  handlers share the unit of work, and a throw rolls it back ([abp-local]). Its `AbpDbContext.SaveChangesAsync`
  override walks change-tracker entries ([abp-dbcontext], L202–222, L424–490) and records deduplicated events on the
  current unit of work ([abp-helper], L133–168). Distributed auto events go out at the end of the unit of work or
  through the outbox ([abp-distributed]). With `DeleteDirectAsync`, soft-delete, multi-tenancy and audit logging
  "won't work" ([abp-repos]); **inference:** no entity events either, nor for `ExecuteUpdate` or raw SQL.
- **EFCore.Triggered.** Save triggers run from a `SaveChanges` interceptor ([triggered-src]); before- and after-commit
  triggers are raised by the application around its own commit ([triggered-readme], L196–198). The stable line is
  v3.2.2 (2022), with v4 in preview ([triggered-releases]).
- **Marten.** The async daemon advances a high-water mark over the event sequence, holds at a gap while a transaction
  that could fill it is running, and elects projection leaders through advisory locks ([marten-daemon]).
  **Inference:** the worked example of polling a sequence-keyed log without losing late commits.

## 8. Node and TypeScript

- Prisma removed `$use` middleware in 6.14.0 for client extensions ([prisma-6.14]). Query extensions wrap model
  operations and raw queries; for raw queries `model` is undefined ([prisma-ext-query]). **Inference:** an extension
  knows a typed operation's model, not raw-SQL tables, database cascades or other clients.
- Pulse used change-data capture to stream "any database change events" through Prisma Client ([pulse-docs], L36) and
  required PostgreSQL 12+ with logical replication ([pulse-limits], L23). In March 2025 its docs were removed as
  "outdated content" and redirected to Prisma Postgres ([pulse-removal], [pulse-redirect]). I found no first-party
  rationale for CDC. **Inference:** CDC was the only way to include writes from other clients, and the removal
  suggests the product was discontinued.
- Drizzle has no lifecycle hooks; a request is open ([drizzle-hooks]). Kysely plugins transform each query's
  operation-node tree before execution ([kysely-plugin], L8–12); **inference:** that reveals the table of a typed
  update or delete, not of raw `sql`.

## 9. Database- and CDC-level systems (contrast)

- **PostgreSQL triggers** fire per row or per statement (a statement trigger fires even for zero rows); `TRUNCATE`
  triggers are statement-level; `AFTER STATEMENT` triggers can read transition tables; foreign-key actions run as
  ordinary `UPDATE`/`DELETE` on the referencing table, so its triggers fire ([pg-triggers]). SQLite has only
  `FOR EACH ROW` triggers ([sqlite-triggers]).
- **Replication slots** keep WAL and catalog rows while no consumer is connected, in extreme cases until the server
  shuts down to prevent wraparound ([pg-logical]).
- **Supabase Realtime** polls the WAL through a temporary logical slot and filters per subscriber; broadcast-from-
  database streams `realtime.messages` inserts off a slot ([supa-arch], L21, L60–68, L195). Filtering deletes needs
  `REPLICA IDENTITY FULL` ([supa-pgchanges]).
- **Electric** creates a publication and the slot `electric_slot_default` with a `REPLICATION` role
  ([electric-deploy]). **Zero** needs PostgreSQL 15+ with `wal_level = logical`, and without event triggers any schema
  change resets every client ([zero-pg]).
- **Debezium** reads committed changes through `pgoutput` ([debezium-pg]). Its former lead recommends an outbox so that
  consumers do not break when "altering the internal domain model" ([debezium-outbox]).
- **PostGraphile** subscriptions (v4, v5) use LISTEN/NOTIFY fed by triggers the application writes ([pgl-v4-subs],
  [pgl-v5-subs]); live queries used logical decoding in v4 (fan-out §1.6) and are not in v5 yet ([pgl-v5-live]).
- **Hasura** live queries poll (fan-out §1.1); event triggers are generated row triggers calling
  `hdb_catalog.insert_event_log` ([hasura-trigger-sql], L1–38), filling the table that stores "all captured events"
  ([hasura-cleanup]).
- **Meteor** writes through its Mongo connection call `Meteor.refresh({collection, id})` ([meteor-conn-src], L242–257,
  L269–282); the polling driver reacts to those and otherwise polls every 10 s ([meteor-polling-src], L14, L68–79).
- **Convex** transactions accumulate a write set; the committer is the sole log writer, and the subscription manager
  walks the log once against active read sets ([convex-how]).

## 10. Other patterns

- **Declared reads, explicit writes.** Redwood live queries call `context.liveQueryStore.invalidate("Auction:123")` or
  `"Query.auctions"` from services, jobs or webhooks ([redwood-rt]); Next.js tags reads (`cacheTag`, `next.tags`) and
  calls `revalidateTag` from server functions ([next-tags]). Both match ADR-0091's read side.
- **Static write-set inference:** none found. Closest are Ash, whose actions are declared on the resource they write,
  and Convex, which collects write sets at run time.
- **The unit of work as the publication point:** ABP, Spring `AFTER_COMMIT`, Ash's queue, Django `on_commit`, Rails
  7.2 `after_all_transactions_commit`.

## 11. Options for Elarion

ADR-0091 needs, per committed transaction, the (source type, partition) pairs it wrote: no row images, no diffs, no
ordering, and a wrong answer is cheap (ADR-0091 §2). **Inference:** every layer can produce that; the options differ in
coverage, fragility and operating cost.

| Write | (a) ORM interceptor | (b) handler boundary | (c) explicit | (d) triggers | (e) CDC |
| --- | --- | --- | --- | --- | --- |
| tracked EF entries | yes | declared or inferred | if written | yes | yes |
| cascades in the EF model | yes | declared or derived | if written | yes | yes |
| cascades only in the database | no | no | no | yes | yes |
| `ExecuteUpdate`/`ExecuteDelete` | via tag (spike pending) | declared | if written | yes | yes |
| raw SQL through EF, SQL tier | hand report | declared | if written | yes | yes |
| jobs, actors, hosted services | yes, for EF writes | no | if written | yes | yes |
| other apps, migrations, psql | no | no | no | yes | yes |
| needs commit tracking in the app | yes | partly | yes | no | no |

**(a) ORM interceptor (ADR-0091 today).** `SaveChanges` and transaction interceptors per scope, cascades from the
model, a `QueryCompilationStarting` tag plus a command interceptor for `Execute*`, hand reports for the rest.
- Catches every tracked EF write wherever a context is used. Misses unreported raw SQL and SQL tier, triggers, other
  applications, and precompiled queries.
- Fit: the tracked part uses public APIs Elarion already attaches (integration events, outbox, tenant scoping,
  auditing). The `Execute*` part relies on the interceptor EF calls easy to get wrong ([ef-interceptors]), on cache
  behaviour and SQL-comment scanning, and not on the AOT path ([ef-aot]); an `IQueryExpressionInterceptor` matching
  `ExecuteDelete` was on ADR-0090's list.
- Precedent: Rails, Laravel, Django, Hibernate, ABP for tracked writes; none for set-based statements.

**(b) Handler and command boundary.** Commands declare `[Writes<Invoice>]`, or the generator infers it from `DbSet<T>`
mutations and `Execute*<T>` in the body; a decorator adds the types to the unit-of-work change set, which
`TransactionDecorator` publishes after commit ([elarion-uow]).
- Catches whatever a declared handler writes, by any mechanism, without EF internals. Misses writes outside the
  pipeline (scheduled jobs are plain `[ScheduledJob]` methods [elarion-jobs]; method-form consumers, actors, hosted
  services) and undeclared writes, which recreate ADR-0091's first problem one level up. No-op commands over-notify.
- Fit: AOT-safe and tier-neutral, a declaration like `[RequeryOn]`. Inference belongs in an analyzer that checks the
  declaration, not in the generator (ADR-0091 §10's keystroke-cost reason).
- Precedent: Ash actions, Spring Modulith service events, ABP's unit-of-work event records.

**(c) Explicit publishing.** `IRequeryHints.Changed<Invoice>()` after every write, as Phoenix and Lustre do.
- Catches what is written down, in any tier and host; misses what is forgotten. It is ADR-0091's rejected per-writer
  path made cheaper (no integration event, projection or topic list).
- Fit: trivially AOT-safe; tracked writes cannot be checked statically. Precedent: Phoenix, Lustre, Redwood, Next.js,
  Django `on_commit`.

**(d) Database triggers.** One `AFTER … FOR EACH STATEMENT` trigger per published table inserts `(table, tenant)` from
the transition tables into a change table deduplicated per transaction; a node polls it and feeds the existing merger.
A trigger calling `pg_notify` instead dedupes within a transaction but takes the commit lock ADR-0091 rejects
(fan-out §2.5).
- Catches every database write: tracked, `Execute*`, raw SQL, SQL tier, database cascades ([pg-triggers]), other hosts,
  applications and data migrations. **Inference:** rows appear only on commit, so ADR-0091's commit tracking (EF
  transaction interceptor, `SqlUnitOfWork` hooks, `TransactionScope`, early publish under raw ADO.NET) disappears.
  Misses `TRUNCATE` without a truncate trigger, and untriggered tables.
- Fit: schema objects the migration plan must generate and keep in step (a startup check against `pg_trigger`);
  PostgreSQL-only for statement triggers ([sqlite-triggers]); tenant logic in SQL; one extra insert per watched
  statement, cleanup, and gap-safe polling ([marten-daemon]). No EF internals; ADR-0090 already accepts triggers.
- Precedent: Hasura event triggers, Supabase broadcast-from-database, PostGraphile subscriptions.

**(e) CDC.** A role holder (ADR-0049, ADR-0050) reads `pgoutput` from one slot and maps relations to source types;
partitions come from the new tuple's tenant column, and deletes need `REPLICA IDENTITY FULL` or the tenant in the key
([supa-pgchanges]).
- Catches everything committed, in commit order. Fit: past this tier (ADR-0025): `wal_level = logical` and a restart,
  WAL retained while the consumer is down ([pg-logical]), managed-database permissions, no SQLite. ADR-0091 already
  keeps it as a replacement producer. Precedent: Supabase, Electric, Zero, Debezium, Pulse, PostGraphile v4.

**(f) Hybrids.**
- **f1:** (a) without the tag: tracked entries and model cascades collected automatically, set-based and raw writes
  reported at the call site, an analyzer enforcing those reports, publication after commit.
- **f2:** f1 plus an opt-in database producer, (d) or (e), behind the same publication format.
- **f3:** static inference as a check: an analyzer derives each method's write set and flags an unreported set-based
  or raw write; declarations stay the source of truth, as for `[RequeryOn]`.
- **f4:** a client `refetchInterval` on screens that cannot tolerate a missed write (Meteor's poll fallback).

## 12. Recommendation (inference)

Everything in this section is my inference from the survey. **Adopt f1, document f2, and do not make the handler
boundary the primary detector.**

1. **Keep the in-process, after-commit change set and its publication format.** The surveyed frameworks that get
   timing right publish after commit, and Elarion already owns the boundary (`IUnitOfWork`, `SqlUnitOfWork`, the EF
   transaction interceptor).
2. **Keep automatic detection of tracked EF writes and model cascades through public APIs.** It is the layer Rails,
   Laravel, Django, Hibernate and ABP use, it covers jobs, actors and consumers without declarations, and it is not the
   surface ADR-0090 removed.
3. **Make ADR-0091's fallback the default for `ExecuteUpdate`/`ExecuteDelete`.** No surveyed ORM-level framework
   intercepts set-based statements; all document the gap. An analyzer reads the written type from the generic argument
   of `ExecuteUpdate<T>`, flags a missing `IRequeryHints.Changed<T>()` in the method and offers a code fix. That works
   with precompiled queries, needs no spike or per-upgrade tag test, and fails at build time instead of going quietly
   stale: Ash's missed-notification principle moved to compile time.
4. **Keep hand reports for raw SQL and the SQL tier, and warn when a report is made inside a transaction Elarion does
   not track,** as Ash warns about missed notifications, instead of publishing early in silence.
5. **Document (d) and (e) as replacement producers.** A team that wants zero hand reports, or has other writers, has
   one in-tier choice: statement triggers into a change table, paid for in database objects, not EF internals.

Not the default: **(b)**, because writes outside the command pipeline (jobs, actors, method-form consumers) are common
in Elarion and the dependency would move back to the writers; **(d)**, because per-table schema objects, SQL-side
tenant logic, write amplification, a gap-safe poller and an uncovered SQLite path are more machinery than a type-level
hint warrants by default.

## 13. Open questions

- **C# interceptors.** Could the generator intercept `ExecuteUpdate` call sites to record the type automatically? EF's
  precompiled queries put their own interceptors there, and the feature is experimental ([ef-aot]); whether two can
  share a call site was not checked.
- **Analyzer scope.** Exclusions live in the EF model at run time. Flag every mapped type, read an attribute, or apply
  only when the producer package is referenced?
- **Trigger cost.** The extra insert per statement, the dedup key (`pg_current_xact_id()`), cleanup and gap handling
  need a commit-latency benchmark before (d) is documented as supported.
- **Unverified details:** `pgoutput`'s `TRUNCATE` handling and the tenant on `DELETE` under Elarion's key shapes; why
  Prisma removed Pulse (the commits only say "outdated content"); whether Phoenix contexts are meant to run inside
  caller transactions (§1.1 is inference); Ash `notify?` under partially failing `:batch` transactions.

## Sources

Code links are pinned to the commit read on 2026-10-25; hexdocs.pm addresses are listed at their `<package>.hexdocs.pm`
redirect targets. The table after the link definitions groups the labels by source.

[phx-gen-scope]: https://github.com/phoenixframework/phoenix/blob/e36472d96423be6ae17c5050dfc63c4c6852fcd4/priv/templates/phx.gen.context/schema_access_scope.ex.eex
[phx-scopes]: https://phoenix.hexdocs.pm/scopes.html
[postgrex-notif]: https://postgrex.hexdocs.pm/Postgrex.Notifications.html
[ecto-changelog-1.1]: https://github.com/elixir-ecto/ecto/blob/v1.1.0/CHANGELOG.md
[ecto-1.1-post]: https://blog.plataformatec.com.br/2015/12/ecto-v1-1-released-and-ecto-v2-0-plans
[ecto-repo]: https://github.com/elixir-ecto/ecto/blob/c0c99f51ef1dc71c265162c92623858027122a77/lib/ecto/repo.ex
[ash-notifiers]: https://ash.hexdocs.pm/notifiers.html
[ash-pubsub]: https://ash.hexdocs.pm/Ash.Notifier.PubSub.html
[ash-pubsub-src]: https://github.com/ash-project/ash/blob/19a13529aa196689c7a268b90d5d08ec9bfb9fa1/lib/ash/notifier/pub_sub/pub_sub.ex
[ash-action-opts]: https://github.com/ash-project/ash/blob/19a13529aa196689c7a268b90d5d08ec9bfb9fa1/lib/ash/resource/actions/shared_options.ex
[ash-src]: https://github.com/ash-project/ash/blob/19a13529aa196689c7a268b90d5d08ec9bfb9fa1/lib/ash.ex
[ash-helpers]: https://github.com/ash-project/ash/blob/19a13529aa196689c7a268b90d5d08ec9bfb9fa1/lib/ash/actions/helpers.ex
[cep-src]: https://github.com/commanded/commanded-ecto-projections/blob/34e7660e2f195f44bb6e1b1948fb9f09502f8355/lib/projections/ecto.ex
[cep-usage]: https://github.com/commanded/commanded-ecto-projections/blob/master/guides/Usage.md
[lustre-sc]: https://lustre.hexdocs.pm/lustre/server_component.html
[lustre-pubsub]: https://github.com/lustre-labs/lustre/blob/f5ac432bb2d625951be4f8731fe446aa03320061/examples/06-server-components/05-publish-subscribe/src/whiteboard.gleam
[pog-src]: https://github.com/lpil/pog/blob/1260e90168cba09acdab3a226ca026c9afb69285/src/pog.gleam
[pog-docs]: https://pog.hexdocs.pm/
[squirrel]: https://github.com/giacomocavalieri/squirrel
[rails-callbacks]: https://guides.rubyonrails.org/active_record_callbacks.html
[rails-relation]: https://api.rubyonrails.org/classes/ActiveRecord/Relation.html
[rails-7.2]: https://guides.rubyonrails.org/7_2_release_notes.html
[turbo-broadcastable]: https://github.com/hotwired/turbo-rails/blob/37530c08780fa6f6dbb56a633de4b81169bdd174/app/models/concerns/turbo/broadcastable.rb
[laravel-events]: https://laravel.com/docs/12.x/eloquent#events
[laravel-model-bc]: https://laravel.com/docs/12.x/broadcasting#model-broadcasting
[laravel-bc-tx]: https://laravel.com/docs/12.x/broadcasting#broadcasting-and-database-transactions
[laravel-bc-src]: https://github.com/laravel/framework/blob/3f732656b1b97de366bce8ed147077ed11e25248/src/Illuminate/Database/Eloquent/BroadcastsEvents.php
[django-signals]: https://docs.djangoproject.com/en/5.2/topics/signals/
[django-qs]: https://docs.djangoproject.com/en/5.2/ref/models/querysets/
[django-tx]: https://docs.djangoproject.com/en/5.2/topics/db/transactions/
[channels-layers]: https://channels.readthedocs.io/en/latest/topics/channel_layers.html
[hib-events]: https://github.com/hibernate/hibernate-orm/blob/37616099b745e01500f65eb580f9cef270550cdb/documentation/src/main/asciidoc/userguide/chapters/events/Events.adoc
[hib-query]: https://github.com/hibernate/hibernate-orm/blob/37616099b745e01500f65eb580f9cef270550cdb/documentation/src/main/asciidoc/userguide/chapters/query/hql/Query.adoc
[hib-postcommit]: https://docs.hibernate.org/orm/7.2/javadocs/org/hibernate/event/spi/PostCommitInsertEventListener.html
[jpa-spec]: https://github.com/jakartaee/persistence/blob/502a2820d07d784df27a502921b53f1705a4ca6d/spec/src/main/asciidoc/ch04-query-language.adoc
[spring-tx-events]: https://docs.spring.io/spring-framework/reference/data-access/transaction/event.html
[spring-data-events]: https://docs.spring.io/spring-data/commons/reference/repositories/core-domain-events.html
[modulith-events]: https://docs.spring.io/spring-modulith/reference/events.html
[ef-overview]: https://learn.microsoft.com/en-us/ef/core/logging-events-diagnostics/
[ef-events]: https://learn.microsoft.com/en-us/ef/core/logging-events-diagnostics/events
[ef-interceptors]: https://learn.microsoft.com/en-us/ef/core/logging-events-diagnostics/interceptors
[ef-execute]: https://learn.microsoft.com/en-us/ef/core/saving/execute-insert-update-delete
[ef-aot]: https://learn.microsoft.com/en-us/ef/core/performance/nativeaot-and-precompiled-queries
[abp-local]: https://abp.io/docs/latest/framework/infrastructure/event-bus/local
[abp-distributed]: https://abp.io/docs/latest/framework/infrastructure/event-bus/distributed
[abp-repos]: https://abp.io/docs/latest/framework/architecture/domain-driven-design/repositories
[abp-dbcontext]: https://github.com/abpframework/abp/blob/ec3203128fa3520161efe240663b34b146dfe23b/framework/src/Volo.Abp.EntityFrameworkCore/Volo/Abp/EntityFrameworkCore/AbpDbContext.cs
[abp-helper]: https://github.com/abpframework/abp/blob/ec3203128fa3520161efe240663b34b146dfe23b/framework/src/Volo.Abp.Ddd.Domain/Volo/Abp/Domain/Entities/Events/EntityChangeEventHelper.cs
[triggered-src]: https://github.com/koenbeuk/EntityFrameworkCore.Triggered/blob/f941ab9888df39c6d877e3b7685a7e7a86e7abf6/src/EntityFrameworkCore.Triggered/Internal/TriggerSessionSaveChangesInterceptor.cs
[triggered-readme]: https://github.com/koenbeuk/EntityFrameworkCore.Triggered/blob/f941ab9888df39c6d877e3b7685a7e7a86e7abf6/README.md
[triggered-releases]: https://github.com/koenbeuk/EntityFrameworkCore.Triggered/releases
[marten-daemon]: https://martendb.io/events/projections/async-daemon.html
[prisma-6.14]: https://github.com/prisma/prisma/releases/tag/6.14.0
[prisma-ext-query]: https://www.prisma.io/docs/orm/prisma-client/client-extensions/query
[pulse-docs]: https://github.com/prisma/web/blob/a7773a92dfd6ebc0cb72d33839948604d546b402/content/400-pulse/index.mdx
[pulse-limits]: https://github.com/prisma/web/blob/a7773a92dfd6ebc0cb72d33839948604d546b402/content/400-pulse/500-known-limitations.mdx
[pulse-removal]: https://github.com/prisma/web/commit/b3d2b3175da6ee39f183cd3d91dde2ce156eb990
[pulse-redirect]: https://github.com/prisma/web/commit/a9dbffdc58938580ea83d96c0d99b16c11d25aa7
[drizzle-hooks]: https://github.com/drizzle-team/drizzle-orm/issues/2266
[kysely-plugin]: https://github.com/kysely-org/kysely/blob/b506b6390a06f47410d359bf0f640a13251b28df/src/plugin/kysely-plugin.ts
[pg-triggers]: https://www.postgresql.org/docs/current/trigger-definition.html
[pg-logical]: https://www.postgresql.org/docs/current/logicaldecoding-explanation.html
[sqlite-triggers]: https://www.sqlite.org/lang_createtrigger.html
[supa-arch]: https://github.com/supabase/realtime/blob/ce84f7d77b5a2f97288a5a4e3f568021d33badb6/ARCHITECTURE.md
[supa-pgchanges]: https://supabase.com/docs/guides/realtime/postgres-changes
[electric-deploy]: https://electric.ax/docs/guides/deployment
[zero-pg]: https://zero.rocicorp.dev/docs/connecting-to-postgres
[debezium-pg]: https://debezium.io/documentation/reference/stable/connectors/postgresql.html
[debezium-outbox]: https://debezium.io/blog/2019/02/19/reliable-microservices-data-exchange-with-the-outbox-pattern/
[pgl-v4-subs]: https://postgraphile.org/postgraphile/4/subscriptions
[pgl-v5-subs]: https://postgraphile.org/postgraphile/5/subscriptions
[pgl-v5-live]: https://postgraphile.org/postgraphile/5/live-queries
[hasura-trigger-sql]: https://github.com/hasura/graphql-engine/blob/9a8a737206eb2a9e6d8b76dea70a3867c1106676/server/src-rsr/trigger.sql.shakespeare
[hasura-cleanup]: https://hasura.io/docs/2.0/event-triggers/clean-up/index/
[meteor-conn-src]: https://github.com/meteor/meteor/blob/5e845e446c4ae91193b883144408859a2720db9c/packages/mongo/mongo_connection.js
[meteor-polling-src]: https://github.com/meteor/meteor/blob/5e845e446c4ae91193b883144408859a2720db9c/packages/mongo/polling_observe_driver.ts
[convex-how]: https://stack.convex.dev/how-convex-works
[redwood-rt]: https://docs.redwoodjs.com/docs/realtime/
[next-tags]: https://nextjs.org/docs/app/api-reference/functions/revalidateTag
[elarion-uow]: ../../concepts/persistence-and-transactions.mdx
[elarion-jobs]: ../../capabilities/scheduling/index.mdx

| Labels | Source |
| --- | --- |
| [phx-gen-scope], [phx-scopes], [postgrex-notif] | Phoenix generator template and scopes guide; Postgrex docs |
| [ecto-changelog-1.1], [ecto-1.1-post], [ecto-repo] | Ecto 1.1 changelog; José Valim's announcement (Plataformatec); `Ecto.Repo` source |
| [ash-notifiers], [ash-pubsub], [ash-pubsub-src], [ash-action-opts], [ash-src], [ash-helpers] | Ash docs and source |
| [cep-src], [cep-usage] | commanded-ecto-projections source and guide |
| [lustre-sc], [lustre-pubsub], [pog-src], [pog-docs], [squirrel] | Lustre docs and example; pog source and docs; squirrel README |
| [rails-callbacks], [rails-relation], [rails-7.2], [turbo-broadcastable] | Rails guides and API (8.1.4); turbo-rails source |
| [laravel-events], [laravel-model-bc], [laravel-bc-tx], [laravel-bc-src] | Laravel 12.x docs; laravel/framework source |
| [django-signals], [django-qs], [django-tx], [channels-layers] | Django 5.2 docs; Channels docs |
| [hib-events], [hib-query], [hib-postcommit], [jpa-spec] | Hibernate docs source and 7.2 Javadoc; Jakarta Persistence spec source |
| [spring-tx-events], [spring-data-events], [modulith-events] | Spring Framework, Spring Data, Spring Modulith docs |
| [ef-overview], [ef-events], [ef-interceptors], [ef-execute], [ef-aot] | EF Core docs (Microsoft Learn) |
| [abp-local], [abp-distributed], [abp-repos], [abp-dbcontext], [abp-helper] | ABP docs and source |
| [triggered-src], [triggered-readme], [triggered-releases], [marten-daemon] | EntityFrameworkCore.Triggered; Marten docs |
| [prisma-6.14], [prisma-ext-query], [pulse-docs], [pulse-limits], [pulse-removal], [pulse-redirect] | Prisma releases and docs; historical Pulse docs and removal commits |
| [drizzle-hooks], [kysely-plugin] | Drizzle issue tracker; Kysely source |
| [pg-triggers], [pg-logical], [sqlite-triggers] | PostgreSQL 18 docs; SQLite docs |
| [supa-arch], [supa-pgchanges], [electric-deploy], [zero-pg], [debezium-pg], [debezium-outbox] | Supabase, Electric, Zero, Debezium docs and source |
| [pgl-v4-subs], [pgl-v5-subs], [pgl-v5-live], [hasura-trigger-sql], [hasura-cleanup] | PostGraphile docs; Hasura source and docs |
| [meteor-conn-src], [meteor-polling-src], [convex-how], [redwood-rt], [next-tags] | Meteor source; Convex, RedwoodJS, Next.js docs |
| [elarion-uow], [elarion-jobs] | Elarion persistence-and-transactions concept page; scheduling capability page |
