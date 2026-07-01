# ADR-0001: Event dispatch timing and transactional delivery

- Status: Accepted (amended by [ADR-0010](0010-event-bus-is-pub-sub-only.md))
- Date: 2026-06-20
- Related: the in-memory event bus plan (`IDomainEventBus`/`IIntegrationEventBus`,
  `[ConsumeEvent]`), [decorator pipelines](../concepts/decorator-pipelines.mdx)

> **Amendment (ADR-0010):** this ADR originally gave Plane A a request/reply method
> (`IDomainEventBus.RequestAsync`) alongside `PublishAsync`. That was later removed — the event bus is now
> **pub/sub-only**, and request/reply is served by typed dispatch (`IHandlerSender`/`IHandler`). The
> two-plane, transaction-phase decision below stands unchanged; only the `RequestAsync` mentions are
> historical.

## Context

Jakarta EE CDI lets an observer declare *when* it runs relative to the surrounding
transaction:

```java
void onCreated(@Observes(during = TransactionPhase.AFTER_SUCCESS) InvoiceCreated e) { … }
```

with phases `IN_PROGRESS` (default), `BEFORE_COMPLETION`, `AFTER_COMPLETION`,
`AFTER_SUCCESS`, `AFTER_FAILURE`. Spring offers the same via
`@TransactionalEventListener(phase = AFTER_COMMIT)`. The question for Elarion: do we
adopt a `during = …` phase on the subscriber, and how does that relate to the .NET
habit of modeling timing as two event *types* (`InvoiceCreating` / `InvoiceCreated`)?

Three facts about Elarion constrain the answer:

1. **Elarion core owns no transaction.** The framework ships no transaction manager
   and no concrete `TransactionDecorator`. Transactions are an **application**
   concern: the app defines a decorator that opens a transaction on *its own* scoped
   `DbContext` (see [decorator pipelines](../concepts/decorator-pipelines.mdx)).
   CDI/Spring can offer `during = …` only because the container owns the transaction
   manager and exposes a synchronization callback (`Synchronization` /
   `TransactionSynchronization`). Elarion has no such ambient seam to hang a phase on.

2. **AOT / no ambient discovery.** AGENTS.md mandates compile-time generation over
   runtime reflection and forbids hidden runtime discovery. A `during = …` enum would
   require an ambient transaction-synchronization registry that core does not have and
   should not invent.

3. **The bus is designed to be swappable (in-memory now, MassTransit later).**
   MassTransit's answer to "after commit" is the **transactional outbox**, not a
   per-listener phase, and MassTransit has *no* in-process synchronous in-transaction
   observer at all — every message is broker-bound (i.e. delivered after commit). That
   difference is the key signal that Elarion is conflating two genuinely different
   things (see Decision).

### What the verbs actually express (and what they do not)

A common misconception is that the bus verbs are transaction boundaries. They are
**not**. A verb chooses **DI scope + who awaits**; transaction participation is an
*emergent* consequence of sharing the scoped `DbContext`.

| Surface | Scope | Awaited by caller? | Transaction relationship |
|---|---|---|---|
| `IDomainEventBus.PublishAsync` | caller's scope | yes (inline, fan-out) | shares the caller's scoped `DbContext` → **inside the caller's tx** if one is open |
| `IDomainEventBus.RequestAsync` | caller's scope | yes (one responder, reply) | shares the caller's scoped `DbContext` |
| `IIntegrationEventBus.PublishAsync` | deferred (see Decision) | no (records intent) | recorded *within* the unit of work; **delivered after commit** |

Key corrections this ADR records:

- **The verb opens no transaction.** `PublishAsync` reactors are atomic with the
  command only because the app's `TransactionDecorator` already opened a transaction
  on the scope's `DbContext`, and the reactors resolve that *same* scoped `DbContext`.
- **No verb means "synchronously, after a successful commit."** A handler returns
  *before* the outer decorator commits, so a handler cannot itself run after-commit
  code. Only code positioned **after** `CommitAsync()` (the decorator) or a durable
  store drained later (the outbox) can be "after commit."
- **Timing/guarantee is a property of the publication mechanism, not the verb name or
  the event type.** "After commit" is not a different verb — it is *when delivery
  happens*, which is selected by host configuration.

### Is the CDI/Spring `during = …` idea well received?

Widely used and convenient, but with well-documented criticisms: it is a leaky
abstraction; events are **silently lost on rollback**; a slow inline listener stalls
the transaction; it is harder to test; and side effects appear "magically." Even in
the Java ecosystem, the recommended way to do durable cross-boundary after-commit work
(email, calls to other systems) is the **transactional outbox**, *not* an in-process
`AFTER_COMMIT` listener — because the naive listener has the same crash-window failure
as "publish after commit."

### Is the .NET "two event types" approach non-idiomatic?

It depends on *what the two types mean*:

- `Creating` / `Created` (`-ing` / `-ed`) idiomatically signals **cancellable-pre vs
  notification-post** (cf. `FormClosing` / `FormClosed`). That is about *influence /
  veto*, not commit, and **is** idiomatic when the pre-event can still change the
  outcome.
- Using two types purely to encode **before/after commit** is **not** the dominant .NET
  DDD idiom. The idiom is the **domain event vs integration event** split
  (eShopOnContainers): an in-process event handled inside the transaction, and a
  separate cross-boundary event published reliably after commit. That is the model this
  ADR adopts.

## Decision

The core realization is that Elarion is conflating **two fundamentally different
communication needs**. We model them as two explicit planes, plus a delivery ladder
for the second plane. We do **not** add a `during = …` / `AFTER_COMMIT` phase enum.

### Two planes

**Plane A — Domain events (in-process, in-transaction, synchronous).**
`IDomainEventBus.PublishAsync` / `RequestAsync`. Run **inline, in the caller's scope**,
so they share the open transaction's `DbContext` and are atomic with the command. A
subscriber failure **fails the command**; `RequestAsync` can veto/reply. This is the
`InvoiceCreating` case and the CDI `IN_PROGRESS`/`BEFORE_COMPLETION` phases. It is
**in-process by nature** — even a future MassTransit backend runs these locally; they
are never broker messages and never go through an outbox.

**Plane B — Integration / notification events (deferred, after commit, reliable).**
`IIntegrationEventBus.PublishAsync`. Records the *intent to publish* **within the unit
of work**; the subscribers (or broker) are invoked **after the transaction commits**. A
delivery failure is **retried independently and never fails the command**. This is the
`InvoiceCreated` case and the CDI `AFTER_SUCCESS` phase. This is the only plane a broker
(MassTransit) can model.

This is exactly the DDD **domain event vs integration event** distinction, and it is
what makes "in-memory **or** MassTransit" honest: Plane A never leaves the process,
Plane B is the swappable transport.

### The decisive ordering fix (from MassTransit's Bus Outbox)

The earlier "commit, *then* publish" pattern has a crash window: a failure between
`CommitAsync()` and the publish loses the event. MassTransit's Bus Outbox inverts the
order — the application publishes **before** `SaveChanges`, and a scoped outbox
publisher writes the message **into the same transaction** as the business data; a
delivery service drains it afterward with retry:

```csharp
// MassTransit sample — publish BEFORE save; the bus outbox makes it atomic.
await dbContext.AddAsync(registration);
await publishEndpoint.Publish(new RegistrationSubmitted { … }); // captured, not sent
await dbContext.SaveChangesAsync();                             // message row committed in-tx
```

Elarion adopts this: **Plane B records the publish inside the unit of work, the unit of
work commits atomically, and delivery happens afterward from a store.** No crash window
once the durable tier is used.

### Plane B delivery ladder (same call site, host-selected guarantee)

The application always calls `IIntegrationEventBus.PublishAsync` within its unit of
work. *How* delivery happens is pure host configuration — the call site, the
`[ConsumeEvent]` handlers, and the generated `EventSubscriptionDescriptor`s are
identical across tiers.

| Tier | Mechanism | Guarantee | MassTransit analog |
|---|---|---|---|
| **Direct** | deliver immediately | fires even on rollback; lossy | no outbox |
| **In-memory outbox** | buffer in the DI scope; flush to the in-process pump **only after the unit of work succeeds** | never on rollback; lost on crash | `UseInMemoryOutbox` |
| **Transactional outbox** | write `OutboxMessage` rows **in the same tx** via an EF `SaveChanges` interceptor; a delivery `IHostedService` drains them with retry | durable; survives crash; at-least-once | `AddEntityFrameworkOutbox().UseBusOutbox()` |
| **MassTransit** (future) | `IPublishEndpoint` + MassTransit's EF bus outbox → broker | durable + distributed | — |

The **in-memory** default is **commit-gated** (it flushes only on success), which
removes the "fires on rollback" foot-gun a naive fire-and-forget design would have. The
remaining gap (loss on process crash) is closed **only** by the durable tier.

Two mechanisms, one call site — an honest distinction:

- The **in-memory integration tier** flushes *after* commit, driven by EF Core
  interceptors (`SaveChangesInterceptor`/`DbTransactionInterceptor`) in
  `Elarion.Messaging.InMemory`, so it needs the app's DbContext lifecycle but
  no hand-written decorator. Best-effort: buffered, lost on crash.
- The **transactional outbox** must write *inside* the transaction, so it uses an EF
  **`SaveChangesInterceptor`**, not a decorator. This is why the durable tier needs the
  app's `DbContext` — registered the MassTransit way, e.g. `AddElarionOutbox<TDbContext>()`.

### The outbox is an implementation detail of the publisher

We do **not** expose an outbox API to application code. The publish call site is
invariant (`IIntegrationEventBus.PublishAsync` within the unit of work); whether
delivery is direct, in-memory-buffered, or durably persisted is selected by host
registration. The only thing the durable tier must "see" is the app's `DbContext`, to
enlist in its transaction — the same coupling MassTransit's
`AddEntityFrameworkOutbox<TDbContext>` requires. Application handlers, `[ConsumeEvent]`
methods, and generated descriptors never reference an outbox type.

### Layering

```
Elarion.Abstractions (EF-free, transport-neutral)
  IDomainEvent / IIntegrationEvent          — message-plane markers
  IDomainEventBus.PublishAsync/RequestAsync — Plane A
  IIntegrationEventBus.PublishAsync         — Plane B
  EventSubscriptionDescriptor               — the neutral seam
        │
        ├── InMemoryDomainEventBus (Elarion) — Plane A inline
        │      + shared EventSubscriptionRegistry / EventContext
        │
        ├── InMemoryIntegrationEventBus (Elarion.Messaging.InMemory)
        │      Plane B commit-gated in-memory bus + pump, driven by EF Core interceptors
        │
        ├── Elarion.Messaging.Outbox  (durable, no broker)
        │      SaveChanges interceptor → OutboxMessage in tx
        │      OutboxDeliveryService (IHostedService) → in-process [ConsumeEvent]
        │
        └── Elarion.MassTransit (future)
               Plane A inline (local); Plane B → IPublishEndpoint + EF bus outbox → broker
```

`EventSubscriptionDescriptor` stays the shared seam for in-process consumers (InMemory
and EFCore.Outbox). A MassTransit backend maps the **same** descriptors to MassTransit
consumers (one routed consumer per event type fanning out), so neither the annotation
nor the generator changes when the backend changes. The `Elarion.Messaging.Outbox`
package parallels the existing `Elarion.Blobs` / `Elarion.Blobs.PostgreSql` split.

### Can our outbox be backed by MassTransit's outbox?

Yes. `Elarion.MassTransit` would implement Plane B's `IIntegrationEventBus.PublishAsync`
over the scoped `IPublishEndpoint` and configure `AddEntityFrameworkOutbox().UseBusOutbox()` —
MassTransit's outbox *is* ours. The first-party `Elarion.Messaging.Outbox` is
the **broker-free** durable alternative for apps that want durable after-commit dispatch
to in-process `[ConsumeEvent]` handlers without adopting a broker (delivered by our own
`IHostedService`, mirroring `InMemoryScheduler`).

## API surface (the publish side)

The decision on *naming* and *interface shape* follows one principle:

> **Put the *guarantee* in the interface name; put the *cardinality/reply shape* in the
> method name.** Two orthogonal axes, each named exactly once.

This is grounded in the .NET ecosystem: `Publish` means fan-out (1:N) in both MediatR
(in-process) and MassTransit (broker), so the verb cannot carry the guarantee — the
*interface* must. The deferred-verb alternatives (`Notify`, `Post`, `Enqueue`) are vague
or leak mechanism. The well-loved libraries (MediatR's `ISender`/`IPublisher` ISP split,
eShop's domain/integration split) encode the guarantee in the type, not the method.

```csharp
namespace Elarion.Abstractions.Messaging;

// Lightweight markers — the event's plane becomes part of its identity (type-addressed),
// and lets the generator route consumers to the right dispatcher reflection-free.
public interface IDomainEvent;       // Plane A: in-process, in the caller's transaction
public interface IIntegrationEvent;  // Plane B: recorded in the unit of work, delivered after commit

// Plane A — synchronous, caller's scope/tx; a subscriber failure fails the command.
public interface IDomainEventBus {
    ValueTask PublishAsync<TEvent>(TEvent @event, CancellationToken ct = default)
        where TEvent : IDomainEvent;
    ValueTask<Result<TResponse>> RequestAsync<TRequest, TResponse>(TRequest request, CancellationToken ct = default)
        where TRequest : IDomainEvent;
}

// Plane B — recorded in the unit of work, delivered after commit; reliability per host config.
public interface IIntegrationEventBus {
    ValueTask PublishAsync<TEvent>(TEvent @event, CancellationToken ct = default)
        where TEvent : IIntegrationEvent;
    // SendAsync<TCommand> (point-to-point) admitted here later.
}
```

Decisions:

- **Separate interfaces, not one umbrella.** `IDomainEventBus` vs `IIntegrationEventBus`
  state the guarantee at the injection site (ISP — inject only what you use; a MassTransit
  backend implements only `IIntegrationEventBus`). No aggregate `IEventBus` is shipped, so
  there is no ambiguous "which method" surface.
- **Same verbs on each:** `PublishAsync` (fan-out), `RequestAsync` (in-process reply —
  clearer than MediatR's overloaded `Send`), future `SendAsync` (point-to-point).
  **`NotifyAsync` is dropped** — the interface, not the verb, conveys "after commit."
- **Marker interfaces on the messages** make the plane part of the type's identity,
  enforce the correct plane at compile time, and let the `[ConsumeEvent]` generator route
  a consumer by its parameter's marker (`IDomainEvent` → domain dispatcher,
  `IIntegrationEvent` → integration dispatcher) with no reflection. Consumer **role**
  (fan-out vs responder) is still inferred from the return type.

## Recommended pattern

> The API below is the planned design and is **not yet implemented**. When the bus
> ships, this pattern graduates into the published docs
> (`docs/concepts/decorator-pipelines.mdx` or a dedicated events page).

The handler raises both planes' events within the unit of work; it does **not** decide
delivery timing — that is the bus + host configuration. The injected interface names the
guarantee.

```csharp
[Handler("invoices.create")]
public sealed class CreateInvoice(
    AppDbContext db,
    IDomainEventBus domainEvents,
    IIntegrationEventBus integrationEvents)
    : IHandler<CreateInvoice.Command, Result<CreateInvoice.Response>> {

    public async ValueTask<Result<Response>> HandleAsync(Command command, CancellationToken ct) {
        var invoice = Invoice.Create(command.CustomerId, command.Lines);
        db.Invoices.Add(invoice);

        // Plane A — domain event. Inline, SAME scope → SAME tx. A subscriber that
        // writes to `db` commits atomically with this command; a failure fails it.
        await domainEvents.PublishAsync(new InvoiceCreating(invoice.Id, command.CustomerId), ct);

        // Plane B — integration event. Recorded within the unit of work; delivered
        // AFTER commit by the configured tier. Never fires if the command rolls back.
        await integrationEvents.PublishAsync(new InvoiceCreated(invoice.Id, command.CustomerEmail), ct);

        return new Response(invoice.Id);
    }
}
```

The in-memory integration tier is commit-gated automatically: the EF Core interceptors
shipped in `Elarion.Messaging.InMemory` flush the per-scope buffer to the
delivery pump after the DbContext commits and discard it on rollback. The buffer is an
internal implementation detail — there is no public dispatch-scope seam and no
hand-written transaction decorator. Conceptually the interceptors do this:

```csharp
// after a successful SaveChanges / transaction commit:
await scope.FlushAsync(ct);   // hand buffered Plane B events to the pump
// on SaveChanges failure / transaction rollback:
scope.Discard();              // drop buffered Plane B events
```

Host chooses the Plane B guarantee without touching the handler:

```csharp
builder.Services.AddElarionInMemoryEventBus<AppDbContext>();   // domain + in-memory integration (best-effort)
// or
builder.Services.AddElarionDomainEventBus()            // domain (Plane A) only
    .AddElarionOutbox<AppDbContext>();                  // durable transactional outbox (Plane B)
```

> [!WARNING]
> **The in-memory tier is best-effort: after-commit events can be lost on a process
> crash.** With `AddElarionInMemoryEventBus<AppDbContext>()` alone, Plane B events are buffered in memory and
> flushed after commit, so they never fire on rollback — but if the process crashes
> between commit and flush (or before the pump drains), the `InvoiceCreated` event and
> its side effects (e.g. the customer email) are **lost with no retry**. Close this gap
> with the **transactional outbox** (`AddElarionOutbox<TDbContext>()`), which writes the
> event in the *same* transaction as the business data and delivers it with
> at-least-once retry. Do **not** reach for a "direct" / non-gated publish to work around
> this — it reintroduces delivery on rollback.

## Consequences

**Positive**

- Core stays transaction-agnostic, AOT-friendly, and free of ambient
  transaction-synchronization discovery.
- One event type per fact; the subscriber never encodes timing. The two planes name a
  real difference (domain vs integration), matching established DDD practice.
- Plane B's guarantee is a host decision on a fixed call site, so the same code runs
  best-effort in dev and durably in production, and can later target MassTransit
  unchanged.
- The after-commit story aligns with the transactional-outbox model, keeping the planned
  MassTransit swap clean; the outbox stays an implementation detail, not app surface.

**Negative / accepted**

- Two planes are more concept than a single `Publish`; teams must learn which to use
  (atomic in-tx collaboration vs after-commit notification).
- Until the durable outbox tier ships, Plane B is best-effort (the warning above).
- Plane A is intentionally in-process-only and non-durable; cross-process reactions must
  use Plane B.
- The durable tier couples to the app's `DbContext` (unavoidable for transactional
  atomicity, and the same coupling MassTransit requires).

## Deferred follow-ups

The first cut shipped event consumers with a **flat, assembly-wide** registration
(`Add{Assembly}EventConsumers`), exactly mirroring the in-memory scheduler's
`Add{Assembly}ScheduledJobs`. Neither was module-feature-gated.

- **Module feature-gating for scheduled jobs and event consumers — done.** Both
  subsystems now emit a per-module `Add{Module}ScheduledJobs` / `Add{Module}EventConsumers`
  (longest-prefix namespace match), wired into the module's generated `ConfigureDefaultServices`,
  which the bootstrapper calls gated by `IsModuleEnabled`. So when a module is disabled
  (`Modules:{Name}:Enabled = false`), **its scheduled jobs do not run and its events are not
  consumed**. The mechanism is a cross-generator partial-method aggregation
  (`ModuleDefaultServicesGenerator` + per-category filler partials), **not** new manifest entries —
  jobs/events stay out of the `ElarionManifest` codec; only the thin module identity the host already
  has is needed. The flat assembly-wide `Add{Assembly}…` methods were **removed**: like handlers,
  services, and validators, jobs and consumers are module-scoped only (the scheduler still emits the
  assembly-level typed job-references type). A job or consumer whose namespace falls under no module
  is reported (`ELSG010`/`ELEVT003`, symmetric with `ELHTTP003`/`ELRPC001`) and left unregistered.
- **Durable `Elarion.Messaging.Outbox` (Plane B reliability) — done.** Ships a
  transactional outbox `IIntegrationEventBus`: each integration event is written as an
  `OutboxMessage` row in the caller's `DbContext` and committed atomically with the business
  data (discarded on rollback), then a hosted `OutboxDeliveryService` polls, claims via a
  provider-neutral conditional `ExecuteUpdate` lease (safe across instances, reclaimed after a
  crash), dispatches to integration consumers on isolated scopes, and finalizes/purges.
  Delivery is at-least-once, so consumers must be idempotent. Deliberately scoped down from the
  MassTransit reference: no inbox/dedup table, no broker, no provider-specific `SKIP LOCKED` —
  just the essential capture/deliver/retry. An `IOutboxStore` seam keeps the EF Core SQL
  isolated and the bus/dispatcher/delivery loop database-agnostic and unit-testable.
- **A message-broker backend** (e.g. MassTransit) remains a possible future Plane B backend;
  it would likewise implement only `IIntegrationEventBus`. Not currently planned — the EF Core
  outbox covers the durable in-process need.

## References

- Jakarta CDI 4.0 — transactional observer methods (`@Observes(during = …)`).
- Spring `@TransactionalEventListener`.
- MassTransit — [In-Memory Outbox / Transactional (Bus) Outbox](https://masstransit.massient.com/concepts/outbox);
  [Sample-Outbox](https://github.com/MassTransit/Sample-Outbox).
- Transactional outbox pattern (microservices.io); domain vs integration events
  (eShopOnContainers).
- Elarion [decorator pipelines](../concepts/decorator-pipelines.mdx) — the
  application-owned `TransactionDecorator` seam.
