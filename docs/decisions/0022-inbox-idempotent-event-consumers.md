# ADR-0022: Inbox pattern for integration-event consumers (idempotent consumers)

- Status: Accepted
- Date: 2026-07-01 (proposed) · 2026-07-04 (accepted, implemented)
- Related: [ADR-0021](0021-idempotency.md) (the idempotency store/decorator this reuses),
  [ADR-0001](0001-event-transaction-phase.md) (the domain/integration plane split),
  [ADR-0010](0010-event-bus-is-pub-sub-only.md) (consumers are fan-out, `Result<Unit>`),
  the [idempotency concept doc](../concepts/idempotency.mdx) and the
  [events backends doc](../capabilities/events/backends.mdx).

> Originally captured as a proposed design while shipping idempotency (ADR-0021). Implemented 2026-07-04; the
> open decisions are resolved in [Resolution](#resolution-as-implemented) below (A: **both** — the scope-seed
> rail *and* `IEventContext.MessageId`; B: **default-on** with an `[AllowDuplicates]` opt-out). One proposed
> detail was corrected during implementation: an `InProgress` claim is **not** acknowledged as success (see the
> resolution — acking a still-uncommitted winner's message can lose the event).

## Context

Integration events (Plane B) are delivered **at-least-once**: the EF Core outbox's `OutboxDeliveryService` polls,
leases, dispatches, and finalizes — and if the worker crashes after dispatch but before finalizing (or a consumer
throws and the message is retried, or two workers race a lease), the **same event is delivered to a consumer more
than once**. Elarion's own outbox is explicit that "delivery is at-least-once … consumers must therefore be
idempotent." Today that idempotency is the *consumer author's* problem.

The **inbox pattern** (a.k.a. the Idempotent Consumer, microservices.io; MassTransit's `InboxState`; NServiceBus's
outbox dedup) solves it: the consumer records the event's message id **in the same transaction** as its business
writes; a redelivery finds the id already present and is skipped, so the consumer's effect happens **at most once**.
Elarion already ships the *sending* half (the transactional outbox); the inbox is the missing *consuming* half —
together they are the standard **outbox + inbox** pair for exactly-once effect over at-least-once transport.

**The key realization from ADR-0021:** HTTP idempotency keys and the messaging inbox are the *same* mechanism —
"record a unique id for this operation in the same transaction as the effect; skip if it already exists" — differing
only in **where the id comes from**. Command idempotency sources a client `Idempotency-Key` (transport header /
`params._meta`); the inbox sources the **event's message id**. Because the idempotency decorator lives in the handler
pipeline — the exact path a **handler-form** `[ConsumeEvent]` consumer runs through — the inbox is a reuse of the
idempotency store + decorator, not a new subsystem.

## Current APIs and constraints (verified)

- **Consumer forms** (`EventConsumerRegistrationGenerator`, `src/Elarion.Generators/`):
  - **Handler-form** — a class with `[ConsumeEvent]` implementing `IHandler<TEvent, Result<Unit>>` (or the
    `IHandler<TEvent>` sugar). It runs the **full decorator pipeline** (the generated `EventSubscriptionDescriptor.InvokeAsync`
    resolves the *decorated* `IHandler<,>` from DI, built by `HandlerRegistrationGenerator`). **A pipeline decorator
    can attach here.**
  - **Method-form** — a `[ConsumeEvent]` method on a `[Service]` class; invoked **directly**, with **no pipeline**.
    A pipeline decorator *cannot* attach. → the inbox is only available to **handler-form** consumers (documented
    limitation; method-form authors dedup by hand or convert to handler-form).
- **Transaction behavior**: the framework `TransactionDecorator.AppliesTo` (`src/Elarion.Abstractions/Pipeline/`)
  attaches to `ICommand` **and `IIntegrationEvent`** handlers (and not `[Idempotent]`), so an integration-event
  handler-form consumer already runs on a **fresh post-commit scope inside a unit-of-work transaction**. Domain-event
  (Plane A) consumers run **inline in the publisher's transaction** (exactly-once by atomicity) — they need **no
  inbox**, and the inbox decorator must **not** attach to them.
- **Consumer identity**: `EventSubscriptionDescriptor.ServiceType` (`src/Elarion.Abstractions/Messaging/`) is the
  stable per-consumer type. This is Elarion's analog of MassTransit's `ConsumerId`.
- **Stable message id (the load-bearing gap)**: the durable, redelivery-invariant id is `OutboxMessage.Id` (Guid PK,
  `src/Elarion.Messaging.Outbox/OutboxMessage.cs`). **It is NOT exposed to consumers today** —
  `OutboxEventDispatcher.DispatchAsync` has the `OutboxMessage` but builds `OutboxEventContext` from
  `(message, message.CorrelationId)` only. `IEventContext` exposes `CorrelationId`, `Plane`, `Message` — **no message
  id**. `CorrelationId` is `Guid.NewGuid()` per publish (`OutboxIntegrationEventBus`), so **today** it happens to be
  1:1 with the outbox message and stable across redeliveries — the shipped docs' hand-rolled dedup examples key on it
  for exactly that reason. But it is semantically the *tracing* id: its natural evolution is to **flow** from the
  publishing operation across everything it causes (one command → N events sharing one correlation id), which would
  silently break any dedup keyed on it. The durable dedup contract must be `OutboxMessage.Id`; exposing it also frees
  `CorrelationId` to become a flowing tracing id later, and the docs' examples should move onto the message id then.
- **Idempotency store reuse** (ADR-0021): `IIdempotencyStore.TryBeginAsync/CompleteAsync/AbandonAsync` keyed by
  `IdempotencyStoreKey(IdempotencyScope Scope, string Owner, string Key)`, backed by the composite-unique
  `EfCoreIdempotencyStore` (`INSERT … ON CONFLICT (scope, owner, key) DO NOTHING`). The decorator resolves the key via
  `IIdempotencyKeyAccessor` and can be seeded via `IIdempotencyKeySeed` (the write-seam added for the JSON-RPC
  `params._meta` path).

## Why the outbox row cannot be the dedup ledger (the grain mismatch)

The intuition "the consumer marks the outbox row processed in the same transaction as its own writes, so a single
database is already exactly-once" fails on the actual transaction boundaries. The delivery sequence
(`OutboxDeliveryService.ProcessBatchAsync`/`DeliverAsync`, verified):

```text
── publish ──────────────────────────────────────────────────────────────────
publisher tx    business writes + INSERT outbox row       ← atomic (the outbox's whole job)

── consume (delivery worker) ────────────────────────────────────────────────
poll scope      ClaimPendingAsync: lease under a lockId   ← ExecuteUpdate, worker's connection
consumer scope  consumer A: BEGIN … A's writes … COMMIT   ← A's own transaction (TransactionDecorator)
                consumer B: BEGIN … B's writes … COMMIT   ← B's own transaction
poll scope      MarkProcessedAsync(message.Id, lockId)    ← worker's write, after the fact
```

`ProcessedOnUtc` is stamped by the **worker**, per **message**, on its own connection, after every consumer
transaction has already committed. The three duplicate windows follow directly:

1. **Sibling failure (routine).** A commits, B throws → `MarkFailedAsync` → the whole message is redelivered → A
   re-runs. The outbox row cannot record "A done, B pending": it is one row per **event**, while completion is a
   fact per **(event, consumer)** — a grain mismatch, not an implementation gap.
2. **Crash between the last consumer's commit and the finalize.** The lease expires, another worker reclaims, and
   every consumer re-runs.
3. **Lease expiry mid-flight.** A stalled worker's message is reclaimed while its consumers are still executing, so
   two workers run them concurrently (the late finalize is lease-guarded and skipped, but the double execution has
   already happened).

Moving `MarkProcessedAsync` into "the consumer's transaction" cannot close these windows:

- **Fan-out grain**: with N consumers there is no single consumer transaction that can atomically assert all-done.
  Per-consumer status columns on the outbox row would *be* the inbox — denormalized into the publisher's table.
- **Protocol ownership**: the finalize is half of the lease protocol (`lockId` guard, `Attempts`, backoff, parking)
  and belongs to the worker's state machine, not to business transactions.
- **Broker portability**: Plane B is the broker-portable plane. On a real broker the "mark" is an **ack** — a
  protocol frame that can never join a database transaction — so any consumer-marks-the-outbox design dies at the
  seam swap.

The inbox is the same intuition placed at the only grain where atomicity with the effect is possible — a second
ledger beside the first:

| | Outbox row | Inbox row |
|---|---|---|
| Grain | one per **event** | one per **(event, consumer)** |
| Written by | publisher tx (insert), worker (finalize) | the **consumer's own transaction** |
| Asserts | "this event is durably recorded / dispatched" | "**this consumer** has processed this message" |
| Atomic with | the publisher's business writes | the consumer's business writes |

(With exactly one consumer, claiming + working + marking in one transaction does work — that is a single-table job
queue, `FOR UPDATE SKIP LOCKED`. Fan-out pub/sub is what forces the two-ledger split.)

## Decision

Add an **inbox decorator** for **handler-form integration-event consumers**, reusing the ADR-0021 idempotency store,
keyed per **(consumer, message)**:

- **Dedup key** = `IdempotencyStoreKey(scope: Consumer, owner: <consumerId>, key: <messageId>)`, where
  `messageId = OutboxMessage.Id` and `consumerId` is `EventSubscriptionDescriptor.ServiceType` (its full name, hashed).
  **The consumer id must be part of the key** — one event fans out to many consumers, so keying on the message id
  alone would make the first consumer's claim block/skip the others (this is exactly why MassTransit keys on
  `(MessageId, ConsumerId)`). Add an `IdempotencyScope.Consumer` (sibling of `Global`/`CurrentUser`) so consumer rows
  are namespaced from command-idempotency rows in the shared table.
- **Own the transaction** like the idempotency decorator: the inbox decorator composes the `IUnitOfWork`, claims the
  key, runs the consumer so the consumer's business writes commit atomically with the inbox row, and rolls back on
  failure. The plain `TransactionDecorator.AppliesTo` must **exclude inboxed consumers** (extend the existing
  `and not [Idempotent]` exclusion), so a consumer is never wrapped in two transactions.
- **Skip-on-duplicate, don't 409.** A duplicate delivery has no client to receive a 409 — it must be *acknowledged
  and dropped*. On a completed key the decorator returns `Result<Unit>.Success(Unit.Value)` (the consumer is treated
  as already-done), so the outbox marks the message processed and stops retrying. `Result<Unit>` is
  `IResultFailureFactory`, so the ELIDEM001-style guard is satisfied.
- **Success-only, failures retry.** A failed consumer (`EventConsumerFailedException` from a non-success `Result`)
  rolls back → the inbox claim is discarded → the outbox redelivers and retries. Same semantics as command
  idempotency; no `StoreFailures` needed (there is no result payload to replay — consumers are `Result<Unit>`).
- **Source the message id via the dispatch-scope rail, reusing `IIdempotencyKeySeed`.** The outbox dispatcher
  (`OutboxEventDispatcher`) and the in-memory pump (`EventDispatchPump`) seed the per-consumer scope with the message
  id, and the inbox decorator reads it via `IIdempotencyKeyAccessor` — the *same* seam command idempotency uses. This
  keeps the message-id plumbing consistent with how `ICurrentUser` and the transport key already flow, and avoids a
  breaking change to `IEventContext`. (Resolved as **both** — see the Resolution: the seed rail carries the key to
  the decorator, and `IEventContext.MessageId` exposes it to consumers.)
- **Activation** (resolved as recommended — see the Resolution): **automatic for every handler-form integration-event consumer**
  (the MassTransit/NServiceBus default — every consumer is deduped, which is the safe default for at-least-once), with
  an opt-out, rather than an opt-in attribute.

## When is the inbox needed (usage guidance)

The inbox dedups the consumer's **transactional effect** — with it, the consumer's DB writes happen exactly once
per message. It does **not** make a foreign side effect (an SMTP send, a third-party API call) exactly-once: it
removes the *systemic* duplicate sources and narrows the residual to the crash window between the foreign call and
the commit (the ADR-0021 cooperative-recipient caveat, transitively). Per consumer:

| Consumer | Inbox | Why |
|---|---|---|
| Domain-event (Plane A), either form | **Never** — the decorator must not attach | Runs inline in the publisher's transaction; exactly-once by atomicity. |
| Integration, **method-form** | Unavailable | No pipeline to attach to. Convert to handler-form when dedup matters. |
| Integration, handler-form, **in-memory bus** | Low value | Best-effort tier: no redelivery across a crash; the inbox guards only in-process multi-delivery. |
| Integration, handler-form, **outbox** | **Default-on** (decision B), opt-out | Delivery is at-least-once *and* retry is per-message: one failing consumer re-runs every already-succeeded sibling consumer of the same event. |
| …whose only effect is a call to a sink that dedups on a caller-supplied key | Legitimate opt-out | Keying the sink call on the **message id** already makes that effect exactly-once; the inbox would only save the wasted duplicate call. |
| …whose effect is naturally idempotent (a pure upsert on a business key) | Legitimate opt-out | Redelivery converges by itself. |

Why default-on rather than opt-in: the routine duplicate source is **not a crash** —
`OutboxEventDispatcher.DispatchAsync` propagates any consumer failure so the **whole message** is retried,
re-invoking consumers that already succeeded. One buggy or transiently-failing sibling consumer re-runs every
healthy consumer on every backoff attempt; under that contract dedup must be the pit of success.

For an external sink (the `IEmailSender` case), the layers compose rather than compete:

1. **Outbox** — the intent ("send this email") is never lost on a crash and never phantom-sent on a rollback.
2. **Inbox** — the consumer's DB effect is exactly-once; the sink call re-runs only if the process dies between the
   sink accepting and the transaction committing.
3. **Sink idempotency key** — pass the seeded **message id** as the recipient's dedup key (the same id the inbox
   reads from the dispatch scope, so one piece of plumbing serves both layers) to close that last window. A keyless
   protocol (bare SMTP) cannot; the residual is an accepted, documented at-least-once window — crash-only and
   milliseconds wide at Elarion's tier.

The inbox and a keyed sink are complementary, not redundant: the inbox also protects the consumer's co-located DB
writes and saves the duplicate external round-trip. This matrix is reflected in the
[idempotency concept doc](../concepts/idempotency.mdx) and the
[consuming-events doc](../capabilities/events/consuming-events.mdx): the hand-rolled guard-table tier became this
built-in default-on decorator, the "an inbox table is the last resort" guidance flipped to "the inbox is the free
safety net; natural idempotency remains the cheapest opt-out", and the dedup examples key on
`IEventContext.MessageId` rather than the correlation id.

## Resolution (as implemented)

Implemented 2026-07-04. The decision stands as proposed, with the open decisions resolved and one semantic
corrected:

1. **No new decorator — `IdempotencyDecorator` *is* the inbox.** The existing decorator needed only a nullable
   `ICurrentUser` (a delivery scope has no caller) and a `Consumer` branch in owner resolution. Everything else is
   the generated policy: `Scope = Consumer`, `KeyRequired = false` (an un-seeded direct invocation — a test, a
   hand-rolled dispatcher — passes through un-deduped instead of failing 400), `Fingerprint = false`,
   `ConflictBehavior = WaitThenReplay`, `StoreFailures = None`.
2. **`InProgress` is NOT acknowledged as success** (correcting the proposal's "skip on Replay/InProgress"). If a
   lease-race loser acked success while the winner's transaction was still open, the loser's worker could finalize
   the message; a subsequent winner rollback would then lose the event entirely. `WaitThenReplay` gives the safe
   semantics: the loser blocks (bounded) on the winner's uncommitted claim — winner commits → `Replay` → success;
   winner aborts → the loser claims and runs; wait times out → `Conflict` failure → `EventConsumerFailedException`
   → the message backs off and redelivers. Only a *committed* claim is ever acknowledged.
3. **Open decision A resolved as *both*.** The dispatchers seed the message id directly into the delivery scope via
   `IIdempotencyKeySeed` (the same direct-seed shape `JsonRpcDispatcher` uses for `params._meta`; no
   `DispatchScopeContext` change needed — the outbox already has a scope per message, the pump one per envelope),
   **and** `IEventContext.MessageId` (`Guid?`) exposes it to consumers — for keying a downstream recipient's
   idempotency key, and so method-form consumers can hand-dedup with the *right* key. Null on the domain plane; the
   in-memory bus assigns a per-publish id so the consumer-visible contract matches the outbox tier. This also frees
   `CorrelationId` to become a flowing tracing id later without silently breaking dedup.
4. **Open decision B resolved as default-on, with a semantic opt-out.** `HandlerRegistrationGenerator` synthesizes
   the inbox policy for every handler whose request implements `IIntegrationEvent` (unless `[Idempotent]` is
   present — inert + ELIDEM002 there). The opt-out is **`[AllowDuplicates]`** (`Elarion.Abstractions.Messaging`) —
   the consumer declares the *property* ("duplicate deliveries are harmless here": a naturally-idempotent effect,
   or dedup delegated to a message-id-keyed downstream), not a mechanism toggle. This is the consumer-side mirror
   of `[AllowAnonymous]` switching off a default-on guard, making it a house convention rather than a one-off; a
   first-draft `[Inbox(Enabled = false)]` was rejected on review as naming the machinery instead of the
   declaration (and reusing `[Idempotent]` itself was rejected: on commands it means "add the guard", so its
   consumer-side negation would read exactly backwards). Retention is deliberately **not per-consumer** — the
   invariant it serves is transport-scoped (see 8), so the first-draft `RetentionHours` knob was dropped along
   with its diagnostic. Diagnostics: `ELINBX001` (warning — `[AllowDuplicates]` on a non-integration-event
   handler). `TransactionDecorator.AppliesTo` mirrors the attachment exactly: an integration-event handler gets
   the plain transaction back only under `[AllowDuplicates]`.
5. **Soft attachment.** The generated inbox pipeline gates on `sp.GetService<IIdempotencyStore>()`: present → the
   decorator attaches; absent → the consumer runs un-deduped exactly as before this ADR (never a resolution
   failure). The delivery tiers make "present" the default: `AddElarionOutbox<T>` and the in-memory integration bus
   both call `AddElarionIdempotency()` (TryAdd — a durable `AddElarionIdempotencyEntityFrameworkCore` registration
   wins; `Elarion.Messaging.Outbox` now references `Elarion` for it). With only the in-memory store the inbox is
   process-local; pair the outbox with the EF store for dedup that survives restarts. Explicit `[Idempotent]`
   commands keep hard resolution — their author asked for idempotency, so a missing store fails loudly.
6. **Owner discriminator** is generation-time: the consumer's fully qualified name verbatim while it fits the
   store's 128-char owner column, else truncated with a stable SHA-256 suffix — readable in the common case,
   collision-safe always. A `Consumer`-scoped policy with a null `Owner` fails loudly at run time (it would collapse
   all consumers of an event onto one claim). The EF store maps the scope to a `"consumer"` discriminator.
7. **`Result<Unit>` payloads store the success flag only.** `Unit` is registered in no JSON context, so the previous
   `GetTypeInfo(typeof(Unit))` emission would have thrown on an AOT-strict host — a latent bug for `[Idempotent]`
   `IHandler<T>` commands too, fixed for both by a Unit special-case in the generated policy.
8. **Retention** reuses the `IdempotencyKeyPurgeService`; inbox rows self-expire like command keys, after a fixed
   24 h. The invariant stands as proposed: **retention must exceed the delivery tier's maximum retry window** (for
   the outbox: the backoff sum across `OutboxOptions.MaxDeliveryAttempts`, ≈43 minutes at the defaults — 24 h is
   ~33× that), or a still-retrying message could re-run after its row was purged. Per-consumer tuning was dropped
   (see 4): the invariant is transport-scoped, so a per-handler knob had no derivable use case; if a deployment
   ever configures retries beyond 24 h, the additive fix is a single global option, not a per-consumer attribute.
   Cross-package startup validation was likewise skipped: the outbox and the idempotency store do not reference
   each other, and the default margin is ample.

## Alternatives considered

- **Extend `IEventContext` with a `MessageId`** (open decision A) instead of seeding via the scope. Simpler for a
  consumer to read, and arguably where a message id belongs — but it is a change to the event-context contract and
  forces both buses to assign a stable id (the in-memory bus assigns none today). The scope-seed approach is
  non-breaking and consistent with the existing dispatch-scope rail, so it is the recommendation, but exposing
  `MessageId` on `IEventContext` is a reasonable enhancement regardless.
- **A dedicated inbox table/store** (MassTransit's `InboxState` with its own `(MessageId, ConsumerId)` schema) instead
  of reusing `elarion_idempotency_keys`. Reusing the idempotency store is one fewer table/store and directly reuses the
  atomic `ON CONFLICT` claim + purge; a dedicated table would match MassTransit's shape but duplicate infrastructure.
  Reuse is recommended; the `Consumer` scope keeps the rows namespaced.
- **Key on the message id only** (no consumer id) — **rejected**: wrong for fan-out (two consumers of one event would
  collide; the second would see the first's claim and skip its own distinct work).
- **Inbox for method-form consumers** via a dispatcher-level wrapper (not a pipeline decorator) — possible but
  inconsistent with the pipeline model; deferred. Method-form stays un-inboxed; recommend handler-form for
  at-least-once side effects.

## Consequences

- Completes **outbox + inbox** for exactly-once *effect* on at-least-once integration eventing, reusing the ADR-0021
  store, decorator shape, and purge — minimal new surface.
- **Only handler-form integration-event consumers** get the inbox; method-form and domain-event consumers do not
  (domain consumers are already exactly-once inline). This must be documented so authors choose the handler form when
  they need dedup.
- Requires exposing the stable message id to consumers (via the scope-seed rail or `IEventContext`), and a per-consumer
  scope in the outbox dispatcher.
- Inbox retention is coupled to the outbox retry window — a configuration invariant to document/validate.
- The same **cooperative-recipient** caveat from ADR-0021 applies transitively: the inbox makes the *consumer's DB
  effect* exactly-once, but a foreign side effect inside the consumer is still at-least-once unless the recipient
  dedups.
