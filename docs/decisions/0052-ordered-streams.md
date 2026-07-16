# ADR-0052: Ordered streams — a sequencer-owned hub, actor stream methods, and a resumable SSE leg

- Status: Accepted
- Date: 2026-07-12
- Related: [ADR-0044](0044-streaming-requests-and-responses.md) (request-driven streaming handlers; this ADR
  realizes the producer-owned half), [ADR-0043](0043-client-events.md) (the at-most-once
  hint/state tier this deliberately does not replace), [ADR-0042](0042-actors.md) (the mailbox is the
  sequencer), [ADR-0048](0048-single-homed-actors.md)/[ADR-0050](0050-role-holder-proxy.md) (why every
  node can reach the producer without a distributed protocol), [ADR-0025](0025-scale-positioning.md)
  (single-node semantics, seam-replacement beyond).

## Context

Client events (ADR-0043) are **at-most-once hints with monotonicity you build in**: the per-subscriber
buffer drops oldest on overflow, an SSE reconnect loses in-flight events, and the `pg_notify` fan-out has
no replay. That is the right contract for invalidation hints and latest-wins state — and structurally the
wrong one when **element identity matters**: a consumer that needs *the current value and every change
since, in order, with completion* (Reactor's `Flux`/`BehaviorSubject`, Akka's `BroadcastHub`) cannot be
served by a lossy fan-out no matter where the code runs. Moving emission inside an actor turn does not
help: greeting and broadcast travel different transports, so cross-path ordering is unknowable — ordering
without reliability is false comfort.

Reactor and Akka Streams get ordering from two things: a **single sequencer** (one writer per stream) and
a **reliable, demand-aware pipe** (the Reactive Streams contract — buffering, backpressure, completion).
Elarion already owns the first half twice over: the **actor mailbox serializes publishes**, and
**single-homing plus the role-holder proxy/ingress rule put every consumer on the sequencer's node**. The
only missing piece is the pipe — and .NET has a native one that is not Rx:
`System.Threading.Channels` + `IAsyncEnumerable<T>` (pull = demand, ordering by construction, BCL-only).

## Decision

Ship the ordered tier as three small pieces, all in existing packages:

### 1. `StreamHub<T>` (`Elarion` core, namespace `Elarion.Streams`)

A hot, ordered, completable in-memory broadcast owned by the producer:

- **Ordering**: `PublishAsync` assigns a contiguous per-hub sequence and delivers to every subscriber in
  publish order (an internal gate keeps that true even for concurrent publishers; the intended owner is a
  single writer — an actor turn).
- **Atomic replay-then-live**: `Subscribe`/`SubscribeSequenced` attach under the hub lock — the retained
  ring (`StreamHubOptions.ReplayCapacity`, default 1 = `BehaviorSubject`) and every subsequent publish,
  with no seam between them. The greeting race that client events resolve client-side does not exist here.
- **Resume**: `StreamSubscribeOptions.ResumeAfterSequence` replays every retained element newer than the
  value. A gap that outran the ring delivers what remains — loss is a visible sequence jump on
  `StreamItem<T>`, never a silent hole.
- **Per-subscriber overflow strategy** (the Akka `BroadcastHub` lesson — one slow consumer must never
  stall the fan-out unless it asked to): `DropOldest` (conflation, default), `Wait` (true backpressure,
  in-process consumers only), `Cancel` (`StreamLaggedException`, publisher never delayed).
- **Completion and failure**: `Complete()`/`Fail(error)` end every subscription — the signal
  fire-and-forget events don't have. Publishing afterwards throws.

### 2. Actor stream methods (generator + `Elarion.Actors`)

An `[Actor]` method returning `IAsyncEnumerable<T>` becomes a **facade stream** — the Orleans 7+
grain-interface shape, so call sites stay migration-portable like the rest of the actor surface:

- The **attach is a mailbox turn** (fast: typically `_hub.SubscribeSequenced(…)`), serialized with every
  other turn; **enumeration runs off the mailbox** draining the subscriber channel. The generated facade
  defers the attach until enumeration (`ActorStreams.Defer`), once per enumeration — `IAsyncEnumerable`
  semantics, nothing happens until you iterate.
- The facade adds the trailing `CancellationToken` (cancels the queued attach and, linked with the
  enumerator's token, the stream); the actor method itself must **not** take one — the turn token is a
  pooled CTS whose lifetime ends with the attach turn (**ELACT012**, which also rejects `[ConsumeEvent]`
  on stream methods).
- **Lifetime is refCount — a live enumeration retains the activation.** The generated stream turn pins
  the activation until the enumeration ends, so **idle passivation never ends a stream mid-flight**: a
  streaming-only actor with attached consumers stays alive, and the idle window restarts when the last
  consumer leaves (Orleans parity in effect — there, batch pulls are grain calls that refresh activation
  collection). Correctness passivations deliberately ignore retention: a snapshot-conflicted activation
  must die (stale state, ADR-0047) and shutdown drains regardless. For those paths the hub-lifetime rule
  remains: complete hubs in `OnDeactivateAsync`, so consumers observe the end and re-subscribe
  (re-activating the actor) instead of starving on a channel nothing writes to anymore. A re-activation
  is a new hub — sequences restart (a new epoch), so resume is a within-one-hub-lifetime contract.

### 3. The SSE leg (`Elarion.AspNetCore`): `MapElarionStream<T>`

A concrete, AOT-safe map (ADR-0031 shape) over `IAsyncEnumerable<StreamItem<T>>`: each element's
canonical JSON is one SSE event with `id:` = sequence, so the browser's automatic `Last-Event-ID`
reconnect header (or an explicit `?after=`) resumes from the hub's ring — **gap-free across reconnects
within the retained window**, falling back to replay-with-a-visible-jump beyond it. Consumer offsets on
one node with zero infrastructure. Keep-alive and disconnect handling mirror the client-events endpoint;
authorization is the host's (delegate checks or `.RequireAuthorization()`).

### Reach: home-served by design, and `pg_notify` is not the transport

A stream needs its sequencer, so stream consumers connect to the producer's node — which the recommended
topology already guarantees: the streamed routes live under the prefixes the role-holder proxy (and later
the identical ingress rule) sends to the actor home. `pg_notify` is deliberately **not** used here: it has
no ordering across sessions, no replay, an 8 KB cap — and no role, since the rest of the path is one TCP
connection that HTTP already makes reliable and ordered. Cross-node streaming *without* home routing is a
stream-aware transport (StreamRefs/RSocket territory) — the existing replace-the-seam/Orleans trigger, not
a default that grows (ADR-0025).

### When to use which — the tier table

**Client events are the default; a stream is the exception you justify** (the actors-vs-database doctrine,
one level up). The deciding axis is whether a **single live producer per key** exists:

| You need | Producer shape | Use |
| --- | --- | --- |
| Invalidation hints ("re-query") | Any handler, any node, post-commit | Client events (hint tier) |
| Latest-wins live state | Live producer, versioned payload | Client events (payload tier + greeting) |
| Every element, machine consumer | Any handler | Integration events / outbox |
| Every element in order, completion, resume | One sequencer per key (homed actor) | **Streams (this ADR)** |

No sequencer → no ordering source → not a stream. Latest-wins semantics → the payload tier already
suffices (LiveQuotes' dashboard deliberately stays on client events; its `/quotes/{symbol}/stream` is the
ordered sibling for consumers that want the full sequence).

## Alternatives considered

- **Grow client events into an ordered stream** (ack/replay/order on the fan-out). Rejected — it rebuilds
  a broker inside the feature whose point is not needing one, and every hint would pay for guarantees only
  streams need. The two contracts stay separate; neither succeeds the other.
- **Rx.NET / Akka.NET dependency.** Rejected — core stays dependency-light (ADR-0017); Channels +
  `IAsyncEnumerable` are BCL and carry the needed subset (hot broadcast, replay, overflow strategy,
  completion). Apps wanting the operator algebra interop via `System.Linq.Async`/`ToObservable` in one line.
- **`pg_notify` as the stream transport.** Rejected — unordered across sessions, no replay, 8 KB cap; and
  unnecessary given home routing.
- **Emit-inside-the-mailbox as the ordering fix for client events.** Rejected — ordering holds only on the
  weakest tier (single-node in-process broadcaster) and dies at the first buffer drop or reconnect; seams
  are designed for the strongest implementation.
- **A per-subscriber reliable queue (durable offsets in Postgres).** Rejected at this tier — consumer
  offsets in the database is Kafka-shaped machinery; the ring + `Last-Event-ID` covers the reconnect
  window, and beyond it the consumer re-converges explicitly.
- **Making request-driven streams a substitute for this hub.** Rejected — `IStreamHandler<TRequest, TItem>`
  is a cold request pipeline for exports and token output. This ADR's producer-owned live stream retains the
  sequencer, replay, and resume semantics that the request-driven contract intentionally does not provide.

## Consequences

- One new primitive in core (BCL-only, `IsAotCompatible` holds), one generator return shape (ELACT012),
  one endpoint helper. No new package, no new dependency.
- The ordering doctrine becomes two-tiered and teachable: monotonic latest-wins → client events;
  gap-free ordered → streams at the home. Both hold on every deployment shape they permit.
- Streaming actors take on one obligation: complete hubs on deactivation — it covers the correctness
  passivations that retention deliberately does not block.
- The SSE leg's resume contract (`id:` = sequence) is now wire-visible; changing it is a breaking change.
