# ADR-0021: Imperative handler transport mapping (and why HTTP stays concrete)

- Status: Accepted
- Date: 2026-06-30
- Related: [ADR-0020](0020-client-capability-bootstrap.md) (its bootstrap handler is the first consumer),
  [ADR-0006](0006-incremental-source-generator-conventions.md) (AOT/RDG-friendly generation), the
  [transports](../concepts/transports.mdx) concept doc, and the named `HandlerDispatcher` bus.

## Context

Elarion's transport exposure is **declarative and compile-time**: `[Handler(Transports = …)]` (JSON-RPC/MCP),
`[HttpEndpoint]` (REST), and `[McpHandler]`, which `AppModuleDiscoveryGenerator` discovers and turns into the gated
registration/mapping code in the host's compilation.

That only works for a handler **whose class you own** — you put the attribute on the class declaration. It cannot
expose:

- a **framework-shipped** handler (e.g. the client-capability bootstrap of [ADR-0020](0020-client-capability-bootstrap.md));
- a **third-party** handler from a referenced package;
- a handler whose exposure you want to **decide at startup** (per environment/config) rather than bake into source.

For these, the host needs to map a handler onto transports **imperatively**, choosing the surfaces itself — without
owning the class. A naïve answer is a single generic `MapHandler<TRequest,TResponse>(route, transports)`. That is
right for the bus and **wrong for HTTP**, which forces the shape of this decision.

## Decision

### Named bus (JSON-RPC + MCP): a generic imperative seam

The `HandlerDispatcher` bus is already a runtime name→handler registry; `RegisterHandlers` merely pre-populates it
from attributes. So we add a generic, fluent seam used wherever the dispatcher is composed:

```csharp
dispatcher.MapHandler<TRequest, TResponse>("elarion.session", HandlerTransports.JsonRpc);
```

It is name-routed (the host picks *which* transports, no route), and serialization goes through the configured
`JsonSerializerOptions` / `JsonTypeInfo` — there is **no minimal-API source generator in this path**, so a generic
method is fine and stays AOT-safe as long as the request/response `JsonTypeInfo` are registered (a framework feature
contributes its own source-generated resolver alongside the `MapHandler` call).

### REST: no generic seam — concrete, per-handler `Map*` only

ASP.NET Core's **Request Delegate Generator (RDG)** — the minimal-API source generator that keeps endpoints
Native-AOT and trim-safe — only intercepts **statically-analyzable `MapGet`/`MapPost(...)` calls with concrete
delegate and parameter types**. A generic `MapElarionHandler<TRequest,TResponse>(route)` is opaque to RDG, so it
falls back to the reflection-based `RequestDelegateFactory` — incompatible with AOT/trimming and against the
"concrete-typed lambdas, no open generics" rule that the `[HttpEndpoint]` generator already follows.

Therefore HTTP exposure is **always a concrete call**:

- App handlers keep `[HttpEndpoint]`; the generator emits one concrete lambda per handler (RDG-friendly).
- A framework handler that wants REST ships a **hand-authored, concrete** `Map*` extension with concrete request/
  response types, e.g.:

  ```csharp
  public static IEndpointConventionBuilder MapElarionSession(
      this IEndpointRouteBuilder endpoints, string route = "/session") =>
      endpoints.MapGet(route, (
          [AsParameters] SessionRequest request,
          [FromServices] IHandler<SessionRequest, Result<SessionResponse>> handler,
          CancellationToken ct) => /* invoke + ElarionHttpResults */);
  ```

There is exactly **one concrete `Map*` extension per framework-exposed REST handler** — acceptable because such
handlers are few and each is a stable, named capability (the `MapHealthChecks` / `MapIdentityApi` / `MapHub`
precedent).

### The capability-extension pattern

A framework feature therefore ships: `AddElarionX()` (DI — the handler, its dependencies, and its `JsonTypeInfo`
resolver), a bus contribution via `MapHandler<…>` (chained into the host's `RegisterHandlers`), and a concrete
`MapElarionX(route)` for REST. The host opts in and chooses which surfaces it wants. [ADR-0020](0020-client-capability-bootstrap.md)'s
bootstrap is the first consumer.

## Consequences

**Positive**

- Framework, third-party, and startup-decided handlers become exposable — the gap the attribute-only model left.
- The host controls exposure of handlers it doesn't own; the bus seam is fully general.

**Negative / accepted**

- **The asymmetry (generic bus seam vs concrete-per-handler HTTP) is deliberate and AOT-driven, not an oversight.**
  It is recorded here so a future contributor does not "unify" it into a generic HTTP helper and silently break
  Native AOT / trimming.
- Each framework-exposed REST handler needs its own small concrete `Map*` extension (a few lines; few such handlers).
- There are now two ways to expose a handler — declarative attributes (owned handlers) and imperative mapping
  (un-owned). That is intentional: they serve different ownership cases. Docs steer owned handlers to attributes;
  imperative mapping is for the cases attributes structurally cannot reach.

## Implementation (follow-up — not in this ADR)

- `HandlerDispatcher.MapHandler<TRequest, TResponse>(string name, HandlerTransports transports)` in
  `Elarion.Abstractions.Dispatch` (fluent, returns the dispatcher).
- Framework capability extensions author their own concrete REST `Map*` method; **no** generic HTTP mapping API is
  provided, by design.
- A short "exposing a handler you don't own" section in `concepts/transports.mdx`.
