# ADR-0046: Actor event consumers (`[ConsumeEvent]` on `[Actor]` methods → generated relay)

- Status: Accepted
- Date: 2026-07-07
- Supersedes the "events reach actors through hand-written relay consumers" rejection recorded in
  [ADR-0042](0042-in-memory-actors.md) (Consequences, `ELACT007`).
- Related: [ADR-0042](0042-in-memory-actors.md) (the in-memory actor runtime this builds on),
  [ADR-0022](0022-inbox-idempotent-event-consumers.md) (the default-on inbox this must preserve),
  [ADR-0021](0021-idempotency.md) (the Consumer-scoped idempotency decorator reused as the inbox),
  [ADR-0001](0001-event-transaction-phase.md) (the two event planes — only integration events reach
  actors), [ADR-0034](0034-abstractions-holds-contracts-not-implementations.md) (`[ConsumeEvent]` stays a
  contract in Abstractions, actor-key vocabulary lives in `Elarion.Actors`),
  [ADR-0006](0006-incremental-source-generator-conventions.md) (generator conventions).

## Context

Feeding integration events into an actor is a common need — an `OrderPlaced` event should reach the
`OrderFulfillmentActor` for that order. ADR-0042 rejected `[ConsumeEvent]` **on** an actor and required a
hand-written handler-form relay instead:

```csharp
[ConsumeEvent]
public sealed class OrderShippedRelay(IActorSystem actors) : IHandler<OrderShipped> {
    public async ValueTask<Result<Unit>> HandleAsync(OrderShipped e, CancellationToken ct) {
        await actors.Get<IOrderFulfillment>(e.OrderId).OnShipped(e.TrackingCode, ct);
        return Result.Success();
    }
}
```

The rejection rested on one technical claim: **"a generator cannot emit a handler-form consumer for another
generator to pipeline."** Roslyn incremental generators never observe each other's output, so if the actor
generator emitted a relay class, neither `HandlerRegistrationGenerator` (which synthesizes the default-on
[inbox](0022-inbox-idempotent-event-consumers.md) decorator by discovering `IHandler<TEvent, Result<Unit>>` in
*source*) nor `EventConsumerRegistrationGenerator` (which emits the `EventSubscriptionDescriptor`) would see
it. Any direct-consumption sugar would therefore bypass the pipeline and **silently lose inbox dedupe** —
at-least-once outbox redelivery would mutate actor state twice, with a green build. That is a real and
severe failure mode, and it is why the relay was left hand-written.

Two facts, confirmed against the shipped runtime and generators, dissolve that argument:

1. **Generator isolation forbids *reuse*, not *ownership*.** The blocker only bites when generator A emits a
   type that generator B must further process. If **one** generator — the actor generator, which already
   owns the actor class — emits the relay **and** its full decorated registration **and** the subscription
   descriptor, there is no cross-generator handoff. Isolation means it must *replicate* the inbox-chain emit,
   not *depend on* another generator's output. Replication that would otherwise drift is eliminated by
   factoring the inbox-chain + descriptor emit into a **shared helper** in `Elarion.Generators` that all
   three generators call, guarded by a golden test asserting the actor-emitted relay's decorator chain is
   byte-identical to the hand-written one.

2. **The relay is exactly the hand-written relay, generated.** The emitted relay injects `IActorSystem` and
   calls `actors.Get<IFacade>(key).Method(evt, ct)` — the same call ADR-0042's hand-written form makes. It
   uses only the public actor seam, so the hand-written relay remains a faithful drop-down: anything the
   generator emits, you could have typed.

Together these make an actor-owned `[ConsumeEvent]` both safe (inbox preserved) and honest (no capability the
hand-written form lacks) — on better terms than ADR-0042 weighed.

## Decision

Allow `[ConsumeEvent]` on an `[Actor]` **method**. `ActorRegistrationGenerator` emits, per such method, a
handler-form relay consumer and its fully decorated registration, so an integration event is deduped by the
inbox and then delivered into the actor's mailbox. `ELACT007` (which rejected this) is removed.

### `[ConsumeEvent]` stays a pristine, actor-agnostic marker

`[ConsumeEvent]` lives in `Elarion.Abstractions.Messaging` and gains **nothing** — no key knob, no actor
concept. It cannot: Abstractions has no notion of an actor key, and `Elarion.Actors` depends on Abstractions,
never the reverse. Its presence on an actor method is the only trigger the actor generator keys off. The
event type is the method's single event-typed parameter, exactly as for a handler-form consumer.

### The relay routes through the facade, so the consumed method is public

The relay calls the actor through its public facade, so a `[ConsumeEvent]` method must be **`public`** and is
therefore on the facade. A consumed method is inherently **dual-purpose** — directly callable *and*
event-triggered — which is honest: an event handler that mutates actor state is a legitimate actor operation,
and nothing is served by pretending it isn't callable. Non-public + `[ConsumeEvent]` is a diagnostic
(`ELACT009`): make it public.

An off-facade path was considered and rejected. The mailbox does not actually route through the facade
(`ActorWorkItem<TActor, TResult>` calls the concrete activation directly; routing is `key →
IActorMailboxRouter → cell`), so the generator *could* enqueue to an `internal` method that never appears on
the facade. But reaching that method needs a public "get a handle by key" API on `IActorSystem` and a
generator-built work item — a path **no hand-written relay could reproduce**. That breaks the drop-down
guarantee (you could no longer replace the generated relay with an equivalent hand-written one) and grows the
runtime surface purely to serve codegen. Keeping the relay on the public facade means generated code only
automates what you could type — the deciding principle. Actors are module-internal anyway, so the facade is
never a public/network surface; a method being callable in-process is not a boundary crossing.

### Key extraction: infer by type, disambiguate with `[ActorKey]`

The relay must address one activation. The actor's key type `TKey` is already fully resolved at generation
time (from the `IActorContext<TKey>` constructor parameter or `[Actor(KeyType = …)]`), so key extraction
matches a **known** type rather than guessing:

- **Singleton actor** (`keyType is null`): no key; the relay routes on `ActorSingletonKey`.
- **Keyed, exactly one event property assignable to `TKey`**: inferred. `OnPlaced(OrderPlaced e)` where
  `OrderPlaced` has one `Guid` → `Get(e.<thatProp>)`. The happy path is signature-only.
- **Keyed, zero or more than one candidate property**: `ELACT008` demands an explicit selector.

The selector is `[ActorKey(nameof(OrderPlaced.OrderId))]`, an attribute **owned by `Elarion.Actors`** (not a
`[ConsumeEvent]` property — that would violate the layering above). It names the event property that
supplies the key; a name that does not resolve to a property assignable to `TKey` is `ELACT008`.

### Integration events only

Only `IIntegrationEvent` may reach an actor. A domain-event (`IDomainEvent`) consumer runs **inside the
emitting command's transaction and scope**; an actor runs on its own scope and schedule, so awaiting an actor
from a Plane A consumer abandons the same-transaction contract that plane exists for. `[ConsumeEvent]` on an
actor method whose event parameter is a domain event is `ELACT010`. (To react to a domain change, publish an
integration event and consume that, or have the command's handler call the actor after its transaction
commits — unchanged from ADR-0042.)

### Guarantees compose in the right order

The relay is a genuine handler-form consumer, so its Consumer-scoped inbox decorator runs **before** its body
enqueues to the mailbox: at-least-once redelivery is swallowed before anything reaches actor state. If the
actor call fails or times out, the relay's `Result` fails and the outbox retries — which the inbox then
dedupes against the previous attempt. Ordering (inbox → mailbox) and failure semantics are identical to the
hand-written relay because the emitted relay *is* that relay.

### Multiple consumers per event: keyed consumer registration

Dogfooding surfaced a pre-existing framework bug the relay would otherwise trip over. A handler-form consumer
registered its decorated pipeline as an **unkeyed** `IHandler<TEvent, Result<Unit>>`, and every
`EventSubscriptionDescriptor` resolved it with `GetRequiredService<…>()`. Two consumers of one event — two
hand-written, or (now common) a hand-written one plus an actor relay — both registered the same closed
interface, so `GetRequiredService` returned the last: one consumer silently never ran. The registry already
stored and dispatched a descriptor per consumer; only DI resolution collided.

An `IEnumerable`/`GetServices` "invoke all" fix was rejected — it can't work under generator isolation. It
requires exactly one descriptor per event; but consumers of one event can come from **different generators**
(the handler generator for a hand-written consumer, the actor generator for a relay), which cannot see each
other's output to collapse into that single descriptor. Two independent "invoke all" descriptors would
double-deliver, and neither generator can be the sole owner (the actor generator can't know a hand-written
consumer exists; the handler generator emits nothing for an event consumed only by an actor).

The fix keeps the per-consumer descriptor and makes it **self-describing**: every event-consumer handler
registers its decorated pipeline **keyed by its own FQN**, and its descriptor resolves
`GetRequiredKeyedService<…>(thatKey)`. Each descriptor invokes exactly its own consumer; the two generators
never coordinate (each emits its consumer's unique key independently); per-consumer `[ConsumeEvent(Order)]`
and interleaving with method-form consumers are preserved. **Commands and queries stay unkeyed** — exactly
one handler per request, still injectable typed-direct as `IHandler<TReq, TResp>`. Only event consumers
(domain and integration, request implements `IDomainEvent`/`IIntegrationEvent`) are keyed. This is the same
isolation lesson as the relay itself: under Roslyn, self-describing per-item composes; a shared
collect-and-dispatch point does not.

### The self-call footgun

The relay is a *separate handler*, not actor code, so it correctly reaches the actor through the facade. The
footgun is the opposite direction: **inside** an actor method, reuse a sibling method by calling it on `this`
— a direct, in-turn local call — and never route through the facade (`actors.Get<ISelf>(key).Other()`),
which re-enqueues onto the actor's own mailbox and self-deadlocks a non-reentrant actor (the ~30 s
call-timeout backstop fires). Documented now; a `ConfigureAwait`-style analyzer (calling your own facade from
within the actor) is a candidate follow-up.

## Consequences

- The hand-written relay becomes the *fallback*, not the required form. Its three responsibilities (dedupe
  before the mailbox, explicit key extraction, the plane boundary) are preserved — but generated,
  type-checked, and drift-guarded rather than retyped per event.
- The relay's DI registration reuses `HandlerRegistrationGenerator`'s registration emitter: the actor
  generator synthesizes a `HandlerInfo` (request = event, response = `Result<Unit>`, the inbox
  `IdempotentInfo`, everything else empty) and calls the same `GenerateRegistration`. The decorator chain is
  therefore *the same code path* as a hand-written consumer — byte-identical by construction, not by a copied
  emitter — and the actor generator adds only the relay class and the `EventSubscriptionDescriptor`. A golden
  test pins that a generated relay's registration matches an equivalent hand-written consumer's.
- **Event consumers are now registered keyed** (by handler FQN) across all three generators, so any number of
  consumers — hand-written or actor relays — coexist for one event. This fixes a pre-existing silent
  last-wins collision, not just the actor case; commands/queries are untouched (unkeyed, one per request).
- New/changed diagnostics: `ELACT007` **removed** (reversal); `ELACT008` (key cannot be inferred / `[ActorKey]`
  does not resolve to a `TKey`-assignable property), `ELACT009` (non-public + `[ConsumeEvent]` — the relay
  reaches the method through the public facade), `ELACT010` (domain event into an actor). No duplicate-consumer
  diagnostic is needed (the keyed registration makes duplicates work rather than collide).
  `EventConsumerRegistrationGenerator` continues to yield on `[Actor]` classes — it simply does not own
  actor-method consumers.
- `[ActorKey]` is added to `Elarion.Actors`; no other `Elarion.Actors` runtime change (the relay uses only the
  existing public `IActorSystem` seam). `[ConsumeEvent]` in Abstractions is untouched.
- Single-node remains the boundary (ADR-0042/0025): the relay delivers to *this* node's activation. Events
  fan out to every node's bus, so every node's actor activation for a key receives the event — the same
  N-independent-states property actors already have. Cluster-authoritative event-sourced actors remain an
  Orleans/Akka.NET/Proto.Actor move, not a growth of this default.
