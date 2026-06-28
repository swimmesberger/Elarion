# ADR-0010: The event bus is pub/sub-only; request/reply is unified under dispatch

- Status: Accepted
- Date: 2026-06-28
- Related: [ADR-0001](0001-event-transaction-phase.md) (the two event planes — this ADR removes
  request/reply from Plane A), [ADR-0002](0002-cross-module-communication.md) (typed cross-module calls),
  [handlers](../concepts/handlers.mdx), [events](../capabilities/events/index.mdx).

## Context

Request/reply ("send a request, get one typed response") had grown three homes:

- **typed-direct** — inject `IHandler<TRequest, Result<TResponse>>`, `HandlerInvoker.InvokeAsync<,>`, or the
  generated `[GenerateModuleApi]` facade;
- **named** — `HandlerDispatcher.DispatchAsync(name, …)`, the transport seam for JSON-RPC/MCP;
- **on the event bus** — `IDomainEventBus.RequestAsync<TRequest, TResponse>`, which routes by the request
  *type* to a single **responder** (a `[ConsumeEvent]` handler whose response is `Result<T>`, `T ≠ Unit`).

The third one is the odd one out. It is functionally a typed mediator *send* — the responder is just an
`IHandler<TRequest, Result<TResponse>>`, and DI is already the type→handler registry — so it duplicates the
typed-direct path while adding ceremony: the request must be marked `IDomainEvent` (a request is not a
notification), the handler must be marked `[ConsumeEvent]`, and the consumer's *return type* secretly selects
between "subscriber" and "responder". It also muddies the event bus's identity: a bus that does both fan-out
notification **and** single-responder request/reply forces a learner to hold two patterns under one name. In
practice nothing in the framework or samples calls `RequestAsync` — only its own unit tests do.

Domain events are in-process and Plane A (inline in the caller's transaction), so `RequestAsync` is *not*
broker-portable; the "messaging frameworks put request/reply on the bus" precedent (MassTransit, NServiceBus)
comes from **broker-backed** request/reply, which is a different, distributed mechanism.

## Decision

1. **The event bus is pub/sub-only.** `IDomainEventBus` and `IIntegrationEventBus` keep only `PublishAsync`
   (fan-out). `[ConsumeEvent]` has exactly one meaning — **subscribe to a notification** — and a consumer
   must be "no-content": a handler implementing `IHandler<TEvent>` / `IHandler<TEvent, Result<Unit>>`, or a
   `[Service]` method returning `void`/`Task`/`ValueTask`. The "return type selects the role" rule is gone.

2. **`IDomainEventBus.RequestAsync` and the responder role are removed.** With them go the responder fields
   on `EventSubscriptionDescriptor` (`InvokeRequestAsync`, `ResponseType`, `IsResponder`, the
   `EventResponderInvokeDelegate`), the registry's responder table, the generator's responder emission, and
   the duplicate-responder diagnostic `ELEVT004`. `ELEVT002` (method form) and `ELEVT005` (handler form) are
   tightened: a consumer that returns a non-`Unit` `Result<T>` is now invalid on **either** plane.

3. **Request/reply lives in dispatch, keyed by locality.** In-process, route to a handler **by type**:
   - inject `IHandler<TRequest, Result<TResponse>>` directly (ambient scope, the caller's transaction), or
   - inject the new **`IHandlerSender.SendAsync<TRequest, TResponse>(request, ct)`** — a thin typed mediator
     *send* over `IHandler` resolution from the ambient scope, for callers that want one injection point
     instead of one per handler (the precise, typed replacement for `RequestAsync`), or
   - `HandlerInvoker.InvokeAsync<,>` when you only hold the root provider and need a fresh seeded scope
     (a custom transport / background job).

   On the wire, route **by name** through `HandlerDispatcher` (the JSON-RPC/MCP seam). For *cross-module*
   decoupling, prefer a `[ModuleContract]` + `[GenerateModuleApi]` facade (typed) — see ADR-0002.

4. **Broker request/reply is deferred and kept separate.** If a broker backend (e.g. RabbitMQ) ever needs
   RPC, it is added deliberately on `IIntegrationEventBus` (Plane B) as an explicitly *distributed* feature
   — request + reply-to queue + correlation id + timeout — and is **not** conflated with the in-process
   typed dispatch above. In-process typed dispatch resolves a *local* handler from DI and cannot reach a
   remote responder, so the two must not share an API.

## Consequences

- **Two clean axes.** Pub/sub is organized by *transaction phase* (Plane A inline, Plane B post-commit);
  request/reply is organized by *key + locality* (typed/in-process vs named/transport). No mechanism
  straddles both. The event bus does one job; dispatch does one job.
- **Simpler `[ConsumeEvent]`.** One role (subscribe), no return-type branching, fewer diagnostics. A
  non-`Unit` return is now a clear error pointing the author at `IHandlerSender`/`IHandler` for request/reply.
- **Migration (pre-1.0, no compat shim).** A former responder `[ConsumeEvent] class X : IHandler<R, Result<T>>`
  becomes a plain `IHandler<R, Result<T>>` (drop `[ConsumeEvent]`, drop the `IDomainEvent` marker on `R`);
  callers replace `bus.RequestAsync<R, T>(r, ct)` with `sender.SendAsync<R, T>(r, ct)` or direct `IHandler`
  injection. Same in-transaction, full-pipeline semantics, less ceremony.
- **Integration events unaffected** — they were already fan-out only.
- **Trade-off accepted.** The one thing lost is "type-routed request/reply with the request marked as a
  first-class domain *event*". That marking was misleading (a request is not a notification), unused, and
  fully covered by typed dispatch, so the loss is nominal.
