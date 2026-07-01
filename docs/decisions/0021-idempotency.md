# ADR-0021: Idempotency (`[Idempotent]` over a single-transaction, unique-constrained key store)

- Status: Accepted
- Date: 2026-07-01
- Related: [ADR-0009](0009-authorization-building-blocks.md) and [ADR-0016](0016-feature-flag-gating.md)
  (the sibling declarative gates this mirrors), [ADR-0004](0004-handler-result-caching.md) /
  [ADR-0017](0017-dependency-light-core.md) (seam-in-Abstractions / impl-in-package),
  [ADR-0003](0003-decorator-attachment-predicates.md) (`AppliesTo`),
  [ADR-0015](0015-ef-core-transaction-participation.md) (EF transaction participation),
  the [idempotency concept doc](../concepts/idempotency.mdx) (usage).

## Context

A mutating command handler must be safe to retry: a client that times out and re-sends, or a duplicate that
arrives concurrently, should execute the operation at most once and receive the first request's result. The
requirement, stated precisely: **the idempotency key must be stored and checked atomically with the operation
itself.** A check-then-act approach races — two requests with the same key both pass the check and both execute.
The safe pattern is to insert the key **in the same transaction** as the operation and rely on a **unique
constraint** to let the database reject the duplicate.

The framework already composes a per-handler decorator pipeline and has solved this shape twice (authorization,
feature gating): a class-level attribute, a generated decorator, a seam in `Elarion.Abstractions` with the heavy
default in an opt-in package.

## Decision

### A `[Idempotent]` attribute + a generated decorator that owns the transaction

`[Idempotent]` (class-level, `ICommand` only) auto-attaches `IdempotencyDecorator` just inside
authorization/feature-gate/cache-invalidation and outside the handler. Unlike the other gates, the decorator
**owns a unit-of-work transaction**: it claims the key, runs the handler so the handler's writes share that
transaction, and commits both atomically. This is the only placement that makes "store the key in the same
transaction as the operation" literally true while still catching the duplicate and replaying.

### Single transaction, success-only replay, 409 on concurrency (research-backed)

- **Single transaction** (not a separate-transaction/recovery-point state machine). The author of Stripe's
  reference implementation (Brandur) explicitly recommends mapping requests 1:1 to transactions whenever a
  handler only mutates local ACID state; the multi-phase model is needed **only** for non-rollback-able foreign
  side effects. AWS's Builders' Library states the token and mutations must be one ACID operation. The .NET
  inbox pattern (MassTransit, NServiceBus, Wolverine) commits the dedup record in the same transaction as the
  business work. A crash rolls the uncommitted key back, so **no reaper** is needed.
- **Success-only replay** by default: a failed result rolls back, discarding the key, so the same key stays
  retryable. Stripe v2 reversed v1 to re-execute failures; AWS Powertools persists only clean successes. An
  opt-in `StoreFailures = Definitive` stores definitive failures via a **savepoint** (discard the business
  writes, keep the key row) — one transaction, no atomicity loss.
- **409 Conflict** for a concurrent in-flight duplicate (IETF draft-07 §2.6, Stripe, Adyen, Brandur),
  implemented with a short PostgreSQL `lock_timeout`; per-handler configurable to `WaitThenReplay`.
- **Status codes** map to `AppError`: missing key `400`, in-flight `409`, fingerprint mismatch `422`, retry
  replays the stored result.

### No external distributed lock

The durable record lives in the operation's PostgreSQL database, so concurrent duplicates across nodes contend
on the same row. The unique constraint on `(scope, owner, key)` *is* the cross-node serialization point;
`INSERT … ON CONFLICT DO NOTHING` never raises (so the transaction is not poisoned), and `lock_timeout` turns a
blocked claim into a fast `409`. Redlock is unnecessary — and unsafe as a sole mechanism (Kleppmann) — since the
constraint is already the fence. This matches every production system surveyed (MassTransit, NServiceBus,
Stripe, AWS DynamoDB conditional writes).

### One transport-neutral layer

Because the decorator lives in the pipeline (not HTTP middleware), HTTP idempotency keys and the messaging inbox
pattern are unified. Only key capture is per-transport, via the dispatch-scope rail: HTTP `Idempotency-Key`
header, JSON-RPC/MCP `params._meta`, or an in-band `IIdempotentRequest` field.

### Package layout

The attribute, seams (`IIdempotencyStore`, `IIdempotencyKeyAccessor`, `IUnitOfWork`), decorator, and the
framework `TransactionDecorator` live in `Elarion.Abstractions` (dependency-light). Core `Elarion` ships the
in-memory store + dispatch-scope wiring. The durable EF store, its `[GenerateElarionIdempotencyKeys]` generator,
and the retention purge live in the new `Elarion.Idempotency.EntityFrameworkCore`; the EF unit of work in
`Elarion.EntityFrameworkCore.UnitOfWork`. HTTP capture in `Elarion.AspNetCore`.

### Why an `IUnitOfWork` seam rather than `DbContext` directly

Elarion deliberately has **no repository and no `IAppDbContext`** — handlers inject the concrete `DbContext` and
query it directly, because the database *is* application logic, not an abstraction. And EF Core's `DbContext`
already *is* a unit of work (change tracker + `SaveChanges` + `BeginTransactionAsync` giving
commit/rollback/savepoint). So introducing `IUnitOfWork` deserves justification.

The distinction is **data access vs. transaction boundary**. The no-`IAppDbContext` rule governs *application*
data access, which is unchanged: handlers still use the concrete `DbContext` directly, and `IUnitOfWork` is never
touched by application code. `IUnitOfWork` abstracts only the *transaction boundary*, and only for two
**framework-owned decorators** — for one reason: those decorators live in `Elarion.Abstractions`, which is
**EF-free and dependency-light** (ADR-0017). A decorator there cannot take `DbContext` without forcing every
consumer of Abstractions to reference EF Core. This is the exact move `CacheDecorator` makes — it takes the
`IHandlerCache` seam, not `HybridCache` — so `IUnitOfWork` is the transaction analog of `IHandlerCache`, with the
30-line `EfUnitOfWork` in the opt-in sibling package.

It is also the *shared* boundary the general `TransactionDecorator` (for non-idempotent commands) and the
idempotency decorator both compose, so features build on one transaction seam rather than each reinventing
transaction ownership. Folding transaction ownership into `IIdempotencyStore` (a `GetOrExecuteAsync`, mirroring
`IHandlerCache.GetOrCreateAsync`) would remove the seam but also remove the reusable transaction decorator and
couple idempotency to EF for dev/test — rejected because a reusable framework transaction boundary was an
explicit goal.

## Alternatives considered

- **Separate committed transactions + recovery points (full Stripe model).** Necessary only for foreign side
  effects; it reverses the "same transaction" guarantee and reintroduces a stale-`pending` reaper. Deferred: the
  outbox already handles deferred side effects, so the single-transaction model suffices for the common case.
- **HTTP response cache (IdempotentAPI-style).** Stores the serialized HTTP response in a distributed cache and
  needs an external distributed lock. It is `HttpContext`-coupled (can't cover JSON-RPC/MCP/event consumers) and
  not transactionally coupled to the business write. Rejected in favor of the transactional, transport-neutral
  pipeline decorator.
- **Storing the raw HTTP response.** We store the serialized typed `Result<T>` instead, because the observable
  response is a deterministic function of it — so replay reproduces an identical response on every transport.

### Key retention and cleanup

Completed keys are **self-expiring**: on completion the store stamps `ExpiresOnUtc = CompletedOnUtc + retention`,
where retention is the handler's `[Idempotent(RetentionHours = …)]` (default 24h). `AddElarionIdempotencyEntityFrameworkCore`
registers `IdempotencyKeyPurgeService`, a hosted worker that every `IdempotencyPurgeOptions.PollingInterval`
(default 1 hour) runs one `ExecuteDelete` of `completed AND expires_on_utc < now`, served by the
`(completed, expires_on_utc)` index so it stays an indexed probe. **No pending-row reaper is needed** — the
single-transaction model never commits a pending claim (a crash rolls it back), so only completed rows exist and
they self-expire; this is the cleanup payoff of single-transaction over the Stripe separate-transaction model,
which requires a stale-`pending` sweeper.

Two deliberate limitations of the purge, acceptable for typical (single-writer / modest-scale) deployments and
left as optional hardening:

- **The purge runs on every application instance** — unlike the outbox, it does not lease its work, so on N
  instances the delete runs N times per interval. This is *safe* (the delete is idempotent; concurrent deletes
  of the same expired rows no-op) but redundant. A lease/leader gate (as the outbox uses) would remove the
  redundancy.
- **The delete is unbatched** — it removes all expired rows in a single statement. In steady state that set is
  small, but a large backlog (e.g. after a long worker outage) would be one big delete / long-held lock; a
  batched `DELETE … LIMIT n` loop would be gentler at scale.

## Consequences

- Exactly-once command execution against DB-state operations, atomic and crash-safe, with no reaper for the
  single-transaction path.
- The durable store targets PostgreSQL (the `ON CONFLICT` claim); other providers degrade to wait-then-replay.
- Foreign side effects require the outbox plus a **cooperative recipient** for end-to-end exactly-once; this is
  documented, not silently guaranteed.
- Follow-ups: the inbox for `[ConsumeEvent]` integration-event consumers (dedup at-least-once deliveries by the
  event message id + consumer identity — the consuming half of outbox+inbox, designed in
  [ADR-0022](0022-inbox-idempotent-event-consumers.md)), an outbox-derived external idempotency key, gRPC key
  capture, and purge hardening (a lease so only one instance purges, and batched deletes) — see *Key retention
  and cleanup*.
