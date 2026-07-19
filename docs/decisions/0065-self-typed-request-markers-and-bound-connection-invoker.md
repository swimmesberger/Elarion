# ADR-0065: Self-typed request markers enable inferred dispatch; ConnectionHandlerInvoker binds per connection

- Status: Accepted
- Date: 2026-07-19
- Related: [ADR-0010](0010-event-bus-is-pub-sub-only.md), [ADR-0044](0044-streaming-requests-and-responses.md),
  and [ADR-0053](0053-bidirectional-client-connections.md).

## Context

Every typed dispatch entry point (`IHandlerSender.SendAsync`, `HandlerInvoker`, `StreamHandlerInvoker`,
`ConnectionHandlerInvoker`) required both generic arguments, because C# cannot infer `TResponse` from a
request whose type declares nothing about its response. Real adapter code read like
`ConnectionHandlerInvoker.InvokeAsync<ResolveWorldSession.Query, ResolveWorldSession.Response>(services,
connection, new ResolveWorldSession.Query(account), ct: ct)` — the request type named twice, the
response type named once, two ambient values re-passed on every call, and a `ct:` named argument forced
by the optional enrichment parameter that precedes it.

The existing markers (`IRequest`, `ICommand`, `IQuery`) are deliberately non-generic and optional: the
framework reads them structurally for verb inference, decorator constraints, and runtime branching.
A MediatR-style `IRequest<TResponse>` parameter would infer `TResponse` but lose the static `TRequest`,
forcing runtime type dispatch (reflection or a registry with boxing) — contrary to the compile-time,
AOT-first stance. Constraint-based inference (`where TRequest : IRequest<TResponse>`) does not exist in
C#; constraints do not participate in type inference.

## Decision

**Self-typed (CRTP) marker variants.** `Elarion.Abstractions` adds optional generic forms of the
existing markers: `IRequest<TSelf, TResponse>`, `ICommand<TSelf, TResponse>`, `IQuery<TSelf, TResponse>`
(all extending their non-generic marker), and `IStreamRequest<TSelf, TItem>` for stream requests. A
request opts in by naming itself: `record Query(Guid Id) : IQuery<Query, Response>`.

Dispatch entry points add overloads whose request parameter is typed as the marker —
`InvokeAsync<TRequest, TResponse>(IRequest<TRequest, TResponse> request, …) where TRequest : notnull,
IRequest<TRequest, TResponse>`. Because the parameter type carries both type parameters, C# infers both
from the argument's implemented interface, keeping dispatch fully static: the overload casts to
`TRequest` and delegates to the explicit-generic path, so DI resolution of
`IHandler<TRequest, Result<TResponse>>` is unchanged — no reflection, no registry, no boxing.
`IHandlerSender` gains the inferred overload as a default interface method delegating to the typed
`SendAsync`, so implementations and fakes keep compiling. Markers stay optional: marker-free requests
keep the explicit two-generic overloads, and overload resolution prefers the identity-typed parameter
when generics are given explicitly.

The generic markers inherit the non-generic ones, so structural reads (`AllInterfaces` scans for
`Elarion.Abstractions.ICommand`, verb inference, `where TRequest : ICommand`, `request is IQuery`)
observe them unchanged. A mismatch between the declared `TResponse` and the handler's actual response
fails handler resolution (compile-time where the handler is resolved statically, an
`InvalidOperationException` from DI otherwise) rather than dispatching to the wrong type.

**Bound connection invoker.** `ConnectionHandlerInvoker` becomes a sealed instance class constructed
once per connection with `(IServiceProvider, IClientConnectionSink)` — the two values an adapter holds
for the connection's lifetime anyway. Call-site shape:

```csharp
var invoker = new ConnectionHandlerInvoker(services, connection);
var result = await invoker.InvokeAsync(new ResolveWorldSession.Query(account), ct);
```

The instance stores the sink, not a snapshot: each dispatch still reads
`IClientConnectionSink.Connection` exactly once before enrichment, preserving the promotion-race
semantics of ADR-0053. Enrichment moves to explicit overloads (`(request, enrich, ct)`) so the common
no-enrichment call needs no named `ct:` argument; the cancellation token stays last per convention.
The named rail keeps `HandlerDispatcher` as a method parameter (`InvokeNamedAsync(dispatcher, name,
request, ct)`) rather than constructor state, so typed-only codecs carry no nullable dispatcher.

The static entry points are removed rather than kept alongside (pre-1.0 clean-API preference; the
bound form is strictly more ergonomic and the migration is mechanical).

## Consequences

- Connection codecs and cross-handler sends lose all generic-argument and ambient-parameter noise for
  marker-carrying requests; the request declaration names its response once, next to the handler that
  returns it.
- Requests dispatched only through generated transports (HTTP, JSON-RPC, MCP, gRPC mappings) gain
  nothing from the self-typed form; the plain markers remain the sufficient default there. Docs
  recommend the self-typed form specifically for requests dispatched by type.
- The self-type parameter is boilerplate (`IQuery<Query, Response>` names the type inside its own
  declaration) and is not compiler-checked to be the implementing type. The `RequestMarkerAnalyzer`
  closes that gap at build time: a wrong `TSelf` is `ELREQ001` (error — the inferred cast would throw),
  and a handler whose response drifts from the marker's declared `TResponse`/`TItem` is
  `ELREQ002`/`ELREQ003` (warnings — a deliberate second handler with a different response for the same
  request remains legal through the explicit-generic overloads).
- One request type implementing two different `IRequest<TSelf, TResponse>` closures makes the inferred
  overload ambiguous at the call site; the explicit-generic overloads remain available.
- Adapters now hold one small invoker object per connection instead of calling statics; tests and docs
  updated accordingly.
