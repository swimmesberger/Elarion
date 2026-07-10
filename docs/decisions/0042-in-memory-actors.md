# ADR-0042: In-memory actors — mailbox-protected state machines with generated typed facades

- Status: Accepted
- Date: 2026-07-06
- Related: [ADR-0025](0025-distributed-scheduler-coordination.md) (the scale positioning this package
  inherits), [ADR-0006](0006-incremental-source-generator-conventions.md) (generator conventions),
  [ADR-0017](0017-dependency-light-core.md)/[ADR-0034](0034-abstractions-holds-contracts-not-implementations.md)
  (package layering), [ADR-0018](0018-generated-infrastructure-is-framework-named.md) (generated facade naming).

## Context

Elarion has stateless request handling (handlers are scoped, per-call) and cross-instance coordination
(scheduler claims, outbox leases), but no primitive for **long-lived in-memory state mutated sequentially**
— the "state machine per order/session/device" shape. Today an app wanting one hand-rolls a
`Channel<T>` + reader loop or sprinkles locks over a singleton, both of which bury the actual state
machine under concurrency plumbing.

The virtual-actor model (Orleans grains) is the established answer: a plain class per identity, activated
on first message, all messages serialized, passivated when idle. But the established implementations don't
fit Elarion's tier:

- **Orleans** brings clustering, silos, and a runtime that is not Native-AOT-friendly.
- **Akka.NET** is untyped `Tell`/`Ask` over `object` — the awkward, non-discoverable API this design
  exists to avoid — plus a cluster stack Elarion deliberately doesn't target.
- **Proto.Actor** is closest in spirit (fast local actors, virtual-actor "grains" via code gen from proto
  files), but its programming model leaks actor mechanics into user code — most visibly
  `Context.ReenterAfter(task, callback)`, which forces continuation-passing style precisely where a
  developer would write `await`.

The design lineage here is **FeatherAct** (a pre-Elarion experiment): plain `IGrain` classes whose public
async methods are the message surface, a source generator emitting a typed client + a message-switch actor,
and a `Channel`-based mailbox cell (`LocalActorCell`) with Ask-via-`TaskCompletionSource`. Its core ideas
hold up; its mechanics (Scriban templates off `CompilationProvider`, per-method command classes, an Akka
backend) predate Elarion's conventions.

Key insight about complexity budget: most of what makes Orleans hard (reentrancy semantics across a
cluster, distributed deadlock, message serialization, placement) exists **because it clusters**. Elarion's
positioning (ADR-0025: 1–10 nodes, vertical-first, replace-the-seam beyond that) removes the driver. A
single-process actor runtime can be small, fully typed, and AOT-clean.

## Decision

Ship **`Elarion.Actors`**: an opt-in, in-process actor runtime + an `ActorRegistrationGenerator` in
`Elarion.Generators`, under the following model.

### Programming model — "normal C#", the actor is invisible

An actor is a **plain class** marked `[Actor]`. No base class, no message types, no `Receive`. State is
ordinary fields; the message surface is the class's public async methods
(`Task`/`Task<T>`/`ValueTask`/`ValueTask<T>`):

```csharp
[Actor]
public sealed class OrderFulfillmentActor(IActorContext<Guid> context, IEmailSender email)
    : IActorLifecycle {
    private FulfillmentState _state = FulfillmentState.Draft;   // no locks, ever

    public ValueTask OnActivateAsync(CancellationToken ct) => /* load state */;

    public async Task<Result<Unit>> Ship(ShipmentInfo info, CancellationToken ct) { ... }
    public Task<FulfillmentState> CurrentState() => Task.FromResult(_state);
}
```

The generator emits a **public facade interface** (`IOrderFulfillment` — class name minus the `Actor`
suffix, framework-named per ADR-0018) plus an internal implementation whose per-method work items invoke
the actor statically. Callers never see mailboxes:

```csharp
var order = actors.Get<IOrderFulfillment>(orderId);   // IActorSystem — cheap, always-valid address
var result = await order.Ship(info, ct);              // enqueued; runs on the actor's mailbox
```

- **Keyed (virtual) actors**: a constructor parameter `IActorContext<TKey>` (or `[Actor(KeyType = ...)]`)
  makes the actor keyed — one activation per key, created lazily by the first message, passivated after an
  idle timeout (default 5 min), state dropped, re-activated on the next message. `OnActivateAsync`/
  `OnDeactivateAsync` (optional `IActorLifecycle`) are the load/flush hooks. Without a key declaration the
  actor is a process singleton.
- **DI**: each activation owns a service scope; constructor parameters beyond the context resolve from it
  via generated `GetRequiredService` calls (no `ActivatorUtilities`, no reflection).
- **Modules**: actors are module-scoped like handlers/jobs/consumers — a tenth
  `ConfigureDefaultServices` hook (`AddActors`) registers each module's actors gated by
  `Modules:{Name}:Enabled`; an actor outside every module warns (`ELACT003`) and is not registered.
  Actors are module-internal (ELMOD002 applies); expose one cross-module through a `[ModuleContract]`.

### Concurrency — non-reentrant default, Orleans-style `[Reentrant]` opt-in, no `ReenterAfter`

- **Default: strictly non-reentrant.** One message runs start-to-finish; an `await` inside a method holds
  the mailbox. This is the model that "feels like normal C#": every method body is a critical section, and
  state reasoning is trivial. The mailbox is a `Channel<WorkItem>` with a single reader loop — the happy
  path pays no scheduler, no context capture, no interposed machinery (FeatherAct's `LocalActorCell`
  design, kept).
- **`[Reentrant]` (class-level opt-in)** enables Orleans-style **turn-based interleaving**: while one
  message awaits, the mailbox may start another, but every segment (initial call and each continuation)
  runs on a per-activation `ConcurrentExclusiveSchedulerPair.ExclusiveScheduler` — interleaved, never
  parallel. Only reentrant actors pay for the scheduler. Documented caveat (same class as Orleans'):
  `ConfigureAwait(false)` inside a reentrant actor escapes the scheduler and forfeits the guarantee.
- **Proto.Actor's `ReenterAfter` is rejected.** It is the manual, callback-shaped encoding of exactly what
  turn-based interleaving gives declaratively: "let other messages run while this task completes." It
  forces continuation-passing style, breaks `async`/`await` composition and stack traces, and demands the
  actor-model literacy this package exists to hide. `[Reentrant]` subsumes it at equal power; the
  non-reentrant fast path is untouched by its absence.
- **Deadlock backstop**: actor→actor request cycles (A awaits B awaits A) deadlock under non-reentrancy,
  as in Orleans. Every facade call carries a **call timeout** (default 30 s, per-actor
  `CallTimeoutSeconds`, per-call `CancellationToken`) that fails the call with a `TimeoutException` naming
  the actor and method, so a cycle surfaces as a diagnosable error, not a hang. Finer-grained Orleans
  mechanisms (`[AlwaysInterleave]`, `[ReadOnly]`, `[MayInterleave]`, call-chain reentrancy) are deliberate
  non-goals until real usage demands them.

### Backpressure and cancellation

- Mailboxes are unbounded by default; `MailboxCapacity` bounds them with `Wait` (async backpressure,
  default) or `Fail` (`ActorMailboxFullException`). Classic drop modes are rejected: every facade call is
  request/reply, and a silently dropped call is a caller awaiting forever.
- The caller's `CancellationToken` cancels a queued call before execution (the work item is skipped) and
  flows into the running method (linked with the call timeout and the activation's stopping token). Host
  shutdown drains mailboxes gracefully, then cancels in-flight work via the stopping token when the host's
  shutdown token fires; remaining queued calls complete as canceled and `OnDeactivateAsync` runs per live
  activation.

### Observability — traces cross the mailbox like an RPC hop

- `ActivitySource`/`Meter` **`Elarion.Actors`** (`HandlerTelemetry` conventions): a caller-side
  `actor.call {Actor}.{Method}` span parents the actor-side `actor.process` span via the context captured
  at enqueue, so one trace spans the boundary. Metrics: message count/duration, queue wait, activation
  count, active activations.
- **Stack traces survive the boundary without wrappers.** The actor-side exception object is set on the
  call's `TaskCompletionSource`; the caller's `await` rethrows it with the original actor-side frames plus
  the caller's continuation frames ("end of stack trace from previous location"). The runtime never wraps
  exceptions; the actor-side span records them.

### Routing and load balancing — the key is the router

Akka-style router surfaces (round-robin pools, consistent-hash groups, broadcast) are **not** part of this
design. In the virtual-actor model the identity *is* the routing function: consistent hashing collapses to
"address the actor for this key", and a worker pool is keying by a bounded shard id
(`actors.Get<IWorker>(orderId.GetHashCode() % poolSize)` — a documented pattern, not API). Stateless
fan-out work has no business in actors at all; that's handlers plus `Task.WhenAll`. If a future need for a
managed pool emerges, it composes over `IActorSystem` without touching this model.

### Positioning — single-node, honestly

In-memory actors on N nodes are N independent states. This package is documented as a **single-node
vertical concurrency primitive** (like the in-memory scheduler default): correct on one node, useful on a
node-local basis in a small cluster (cache-like, per-node coordination), wrong as shared state across
nodes. Per ADR-0025, the >1-node answer for authoritative distributed actors is **replacing the runtime
with a dedicated engine** (Orleans, Akka.NET, Proto.Actor cluster) — never growing this default toward
clustering. No placement seam is speculatively added; the facade-interface surface is the migration seam
(an Orleans grain interface is shape-compatible with a generated facade).

### Package layout and generator

- `Elarion.Actors`: attributes (`[Actor]`, `[Reentrant]`), contracts (`IActorSystem`, `IActorContext<T>`,
  `IActorLifecycle`, `IActorFacade`), runtime (cells, hosts, hosted-service drain, telemetry), registration
  API (`AddElarionActorSystem`/`AddElarionActor`). Depends only on `Elarion.Abstractions` +
  `Microsoft.Extensions.*` abstractions; `IsAotCompatible` (work items are concrete generated types — no
  reflection, no serialization, messages never leave the process).
- `ActorRegistrationGenerator` lives in `Elarion.Generators` (the single bundled-analyzer home), triggered
  by `[assembly: GenerateActors]`/`[UseElarion]`, discovering via `ForAttributeWithMetadataName` with
  value-equatable models and diagnostics-as-data per ADR-0006 (cache reuse is test-asserted). Diagnostics:
  `ELACT001` invalid type, `ELACT002` invalid method, `ELACT003` not in a module, `ELACT004` ambiguous
  key, `ELACT005` invalid constructor.
- **FeatherAct's per-method command classes and the Akka backend are dropped.** In-process with no wire
  format, a generated work item that directly invokes the method (completing a `TaskCompletionSource`)
  replaces command types + dispatch switch — fewer allocations, no unknown-message path. The Akka backend
  (typed facades over Akka actors) was FeatherAct's answer to Akka's untyped Ask; for Elarion it would
  drag a cluster framework into a package whose scope is explicitly single-node, so it is cancelled — a
  cluster user adopts the cluster framework's own model wholesale.

### Call-path cost model and optimization roadmap

`ValueTask` is supported as a **shape** on both sides (actor methods and facades mirror
`Task`/`Task<T>`/`ValueTask`/`ValueTask<T>` exactly), but its headline win — the allocation-free
synchronously-completed path — structurally cannot apply to an actor call: every call crosses the
mailbox (enqueue → wait for the turn → run → complete the caller), so a facade call is never
synchronously complete. The v1 per-call cost is therefore:

1. the generated **work item** (already cheaper than the FeatherAct design — one object instead of a
   command + reply pair, no dispatch switch);
2. a **`TaskCompletionSource<T>` + its `Task`** — the completion signal, and the piece that makes the
   multi-writer completion races (run loop vs. timeout registration vs. caller-cancel registration)
   trivially safe via idempotent `TrySet*`;
3. an async state machine in `ActorHandle.InvokeAsync`;
4. an async state machine in the generated facade method.

Each candidate was evaluated step-by-step against the `tests/Elarion.Benchmarks` BenchmarkDotNet
project (direct-call baseline, sequential and pipelined asks, non-reentrant vs. reentrant,
actor-to-actor ping-pong, `MemoryDiagnoser`) with a keep-if-the-win-is-real, drop-otherwise rule.
All numbers `--job short` on an Apple Silicon dev machine — directional, not authoritative.

- **Pooled per-call cancellation — shipped.** The first baseline showed the default `CallTimeout`'s
  fresh `CancellationTokenSource` + timer per call, plus the linked source in the run path, as the
  largest single overhead (≈456 B/call together). A single **pooled** source
  (`ActorCancellationPool`, `CancellationTokenSource.TryReset()` recycling, capped at 256) is now
  the invocation token: `CancelAfter` arms the timeout on it, caller cancellation and the stopping
  token cancel it via registrations, and attribution maps a cancellation to
  canceled-vs-`TimeoutException` (run in both the registration and the run loop's cancellation
  catch, so timeout attribution cannot lose the completion race). Happy-path calls never cancel the
  source, so it recycles indefinitely; canceled/timed-out sources are disposed, never recycled.
  Sources are created against the runtime's `TimeProvider`, keeping fake-clock tests deterministic.
  **Effect: default call 1024 → 568 B, latency unchanged.**
- **Sync-enqueue fast path in the handle — shipped.** The unbounded-mailbox enqueue almost always
  completes synchronously; with no trace listener attached, `InvokeAsync` returns
  `new ValueTask<T>(item.Completion)` with no async frame — machine (3) gone, and a `Task`-shaped
  facade's `.AsTask()` unwraps to the underlying TCS task allocation-free. The traced/suspended
  path falls back to the full async helper. **Effect: 568 → 448 B, and pipelined throughput
  0.95 → 0.56 µs/op (~1.8M msg/s on one mailbox) — suspended handle frames dominated under load.**
- **Facade pass-through — shipped.** A wrapper-shaped benchmark variant measured the generator's
  `async`/`await` facade wrapper at +80–120 B/call for zero behavior. The generator now emits
  expression-bodied pass-through: `ValueTask` shapes return the handle's `ValueTask` directly,
  `Task` shapes bridge via `.AsTask()` (free on the fast path above); the emission shape is pinned
  by generator tests. **Effect: removes machine (4) from generated facades.**
- **`IValueTaskSource` work items — evaluated and dropped.** After the three wins a default call is
  448 B / ≈3.3 µs, and the remaining budget is the TCS + `Task` (~130 B), the work item itself, and
  registration/queue-segment amortization. Replacing the TCS with
  `ManualResetValueTaskSourceCore<T>` would save perhaps 150–200 B with **no latency win** (latency
  is scheduling-dominated), while trading away TCS's free, idempotent three-way completion
  arbitration (run loop vs. timeout vs. caller cancel) for a hand-built `Interlocked` state machine
  plus single-consumption and pooling lifetime rules. At the target tier's realistic message rates
  the saved Gen0 churn is noise. Dropped; revisit only if a profiled workload shows actor-call
  allocation as a real cost.

Final indicative numbers: sequential awaited call ≈ 2.6–3.3 µs / 448–480 B (first baseline: 1024 B);
`[Reentrant]` adds ≈ 1 µs / 384 B; pipelined ≈ 0.54 µs/op (~1.8M msg/s per mailbox); actor→actor
ping-pong ≈ 1.7 µs / 480 B per round trip. Direct-call floor: unmeasurable (cached `Task`, zero
alloc).

## Consequences

- State machines become plain classes with zero concurrency plumbing; sequential mutation is a runtime
  guarantee, not a code-review hope. Renames are compile errors (typed facades), and the schema/transport
  surface is unaffected (actors are in-process only; a handler remains the transport-facing gate).
- Two mental models coexist: handlers (stateless, per-call scope, transactional decorators) and actors
  (stateful, activation-scoped, no decorator pipeline). The concept doc draws the line: reach for an actor
  only when in-memory state must survive across calls and be mutated sequentially; otherwise it's a
  handler or a `[Service]`.
- The actor pipeline is deliberately bare — no authorization/validation/idempotency decorators. Actors are
  internal machinery called by already-gated handlers; re-running the pipeline inside would double-gate.
- Non-reentrant + call cycles = timeout errors at the default 30 s. This is by design (surfacing a design
  smell), but the failure is delayed rather than immediate; the docs steer actor→actor interaction toward
  events or short non-cyclic calls, and `[Reentrant]` exists for the legitimate cycles.
- Passivation drops state by design; anything that must survive belongs in `OnDeactivateAsync`-flushed /
  `OnActivateAsync`-loaded storage. A first-class persistence seam (`IPersistentState<T>`-style, Postgres
  sibling) is the obvious phase 2 and nothing in the contracts precludes it. *(Delivered by
  [ADR-0047](0047-actor-state-snapshotting.md): `IActorState<TState>` + `IActorSnapshotStore` with the
  `Elarion.Actors.PostgreSql` default.)*
- A reentrant actor buys liveness with interleaving complexity, and `ConfigureAwait(false)` inside one is
  a real footgun — latent (it only escapes when the await actually suspends) and scoped to actor-owned
  code (libraries called by the actor may use it freely; context capture is per-method). It is therefore
  analyzer-enforced: `ELACT006` flags `ConfigureAwait(false)`/capture-free `ConfigureAwaitOptions` inside
  `[Reentrant]` classes, including lambdas and nested types. State-mutating delegates handed to libraries
  remain undetectable and are covered by documentation only.
- The runtime's single-threaded guarantee is "one turn at a time with happens-before via await", not
  thread affinity — thread-affine native resources need a dedicated scheduler, which is out of scope.
- **Events reach actors through relay consumers, not `[ConsumeEvent]` on the actor (`ELACT007`).**
  *(Superseded by [ADR-0046](0046-actor-event-consumers.md): `[ConsumeEvent]` is now allowed on an actor
  method and the relay is generated — a single generator owning both the relay and its decorated
  registration sidesteps the isolation argument below, and the mailbox never routed through the facade.
  `ELACT007` is removed.)*
  First-class event consumption on actors was considered and rejected: a generator cannot emit a
  handler-form consumer for another generator to pipeline (generators never see each other's output),
  so any direct-consumption sugar would bypass the handler pipeline and **silently lose the default-on
  inbox dedupe** — at-least-once redelivery would mutate actor state twice. A hand-written handler-form
  relay (`[ConsumeEvent]` handler that calls the facade) composes the guarantees in the right order
  (inbox → mailbox), keeps key extraction explicit, and confines actors to integration events (a Plane A
  consumer shares the emitting command's transaction, which an actor structurally cannot). `ELACT007`
  rejects `[ConsumeEvent]` on `[Actor]` classes with that guidance, and the event-consumer generator
  yields to it instead of reporting a misleading "not a [Service]".
- **Adjacent idea, recorded but deliberately not in scope: a `[Sequential]` handler decorator.** The
  keyed-mailbox cells could serialize *handler* executions per request-derived key (e.g.
  `[Sequential(Key = nameof(Request.OrderId))]`) — "one command per order at a time" without moving any
  state in-memory: the handler stays stateless and the database stays authoritative, only execution is
  serialized. It was kept out of v1 for three reasons: (1) the current answer to concurrent same-entity
  commands is already shipped and stronger where it matters — the transaction boundary plus optimistic
  concurrency, and `[Idempotent]` for duplicates; (2) it inherits the same single-node truthfulness
  problem as actors, but on a surface (handlers) that *is* expected to run on every node — per-key
  serialization that silently holds on one node only is a correctness trap, so the honest multi-node
  shape is a claim/lock seam, which is a different design; (3) it risks becoming a lock-shaped crutch
  that hides contention better modeled as either an aggregate check inside the transaction or a real
  actor. If real demand appears, it should arrive as its own ADR reusing the cell/mailbox runtime — the
  attribute surface is cheap, the semantics (queue depth, timeout, gating placement in the decorator
  order, multi-node story) are the actual decision.
