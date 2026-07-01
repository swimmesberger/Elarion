# ADR-0015: EF Core stores enlist in the caller's ambient transaction

- Status: Accepted
- Date: 2026-06-29
- Related: [ADR-0001](0001-event-transaction-phase.md) (event dispatch timing and
  transactional delivery), [persistence and transactions](../concepts/persistence-and-transactions.mdx)

## Context

Elarion ships several EF-Core-backed stores that a handler may call alongside its own
business writes: settings (`EfCoreSettingsStore`), the transactional outbox
(`EfCoreOutboxStore`), resource-sharing grants (`EfCoreResourceGrantStore`), and
PostgreSQL blob storage (`PostgreSqlBlobStore`). Handlers also raise domain and
integration events (ADR-0001).

For these to compose with application code, a write a store performs **must be part of
the same unit of work** as the surrounding command — when a handler wraps work in
`db.Database.BeginTransactionAsync()` and rolls back, the store's write must roll back
too; when it commits, the store's write must commit with it. This is not automatic for
every implementation technique: a store that opens its own database connection, or that
runs raw ADO commands without enlisting them, would silently escape the caller's
transaction. Several stores deliberately bypass the EF change tracker for performance
(`ExecuteUpdate`/`ExecuteDelete`, raw `INSERT`, raw Npgsql `bytea` streaming), which is
exactly where enlistment can be lost.

## Decision

**Every Elarion EF Core store operates on the caller-injected `TDbContext` and rides
`Database.CurrentTransaction`. None opens an escaping connection.** A store write issued
on a context that already holds an open transaction commits or rolls back with it.

The enlistment technique per store:

- **Change-tracker writes** (`Add` + `SaveChanges`, `ExecuteDelete`) and **set-based
  writes** (`ExecuteUpdate`, `ExecuteDelete`, `ExecuteSqlRaw`) run on the injected
  context, so EF Core executes them on the context's connection and current
  transaction. Used by settings, grants, and outbox.
- **Raw Npgsql** (the blob store's `bytea` content I/O) reuses EF's connection
  (`Database.GetDbConnection()`) and **explicitly enlists** each command via
  `command.Transaction = dbContext.Database.CurrentTransaction?.GetDbTransaction()`. The
  blob store opens its *own* transaction only when the caller has none, so metadata and
  content always commit atomically.

Two deliberate exceptions, both correct:

- **The outbox delivery loop** (`OutboxDeliveryService`) runs on its **own fresh DI
  scope**, after the producing transaction has committed. That is the point of the
  outbox — capture in the caller's transaction, deliver afterward (ADR-0001). The
  *capture* (`Append`) enlists; the *delivery* intentionally does not.
- **In-memory integration events** are commit-gated by EF Core interceptors
  (`EventDispatchSaveChangesInterceptor` / `EventDispatchTransactionInterceptor`): the
  per-scope buffer flushes to the delivery pump on `TransactionCommitted` (or, with no
  ambient transaction, after an autocommit `SavedChanges`) and is **discarded on
  rollback**. Domain events dispatch inline in the caller's scope, so they are already
  inside the caller's transaction with no gating of their own (ADR-0001).

### Attaching the in-memory interceptors

The commit-gating interceptors are scoped services, and EF Core does **not** auto-discover
application-DI `IInterceptor` services, so they must be added to the context's options.
`AddInMemoryEventBus<TContext>()` / `AddInMemoryIntegrationEventBus<TContext>()` do this
automatically: they register an `IDbContextOptionsConfiguration<TContext>` that calls
`AddInterceptors(sp.GetServices<IInterceptor>())` from the context's own scope (so the
interceptors share the same `EventDispatchScope` the bus buffers into). A plain
`AddDbContext<TContext>()` is then all the host writes:

```csharp
services.AddInMemoryEventBus<AppDbContext>();
services.AddDbContext<AppDbContext>(o => o.UseNpgsql(connectionString));
```

A low-level non-generic `AddInMemoryIntegrationEventBus()` registers the building blocks
without attaching the interceptors, for hosts that attach them by hand (or exercise the
bus without a database). The durable outbox tier does not use these interceptors — it
commit-gates through its own `SaveChanges` interceptor and the database transaction.

## Consequences

**Positive**

- A handler composes a store write with its own business write in one transaction; a
  rollback is all-or-nothing across both. This is verified by Testcontainers-backed
  regression tests (commit-persists / rollback-discards) for each store and for the
  in-memory integration tier.
- The blob store's cascade FK (`blob_contents` → `stored_blobs`, `ON DELETE CASCADE`)
  means a delete removes the content row within the same transaction — confirmed by a
  raw-table assertion, so there is no orphaned content.

**Negative / accepted**

- The raw-Npgsql blob path requires an Npgsql connection/transaction; it throws a clear
  error against a non-PostgreSQL provider rather than silently running unenlisted.
- The in-memory integration tier's commit-gating depends on the interceptors being
  attached to the context; the `AddInMemoryEventBus<TContext>()` overload does this
  automatically, so the host only registers the context normally.
