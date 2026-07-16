# ADR-0063: gRPC ships as typed unary and server-streaming transport adapters

- Status: Accepted
- Date: 2026-07-16
- Related: [ADR-0005](0005-cross-module-error-channel.md),
  [ADR-0017](0017-dependency-light-core.md), [ADR-0031](0031-imperative-handler-transport-mapping.md),
  and [ADR-0044](0044-streaming-requests-and-responses.md).

## Context

An Elarion gRPC method must authenticate its call, carry request-boundary state into a fresh dispatch
scope, invoke the DI-resolved decorated handler chain, and translate a failed `Result<T>`. Repeating
that sequence in every generated gRPC override risks bypassing current-user initialization, cancellation,
or decorators. Protobuf messages are wire contracts, however; putting them in handlers or inferring
field mappings would couple modules to field numbering, presence, and versioning decisions.

## Decision

Ship `Elarion.Grpc`, a host-neutral package with typed unary and request-driven server-streaming entry
points:

- The injectable `GrpcHandlerInvoker.InvokeUnaryAsync<TRequest,TResponse>` primarily accepts an
  already-mapped application request plus the exact `ServerCallContext`, and returns the application response.
  A second convenience overload accepts the wire request plus explicit wire-to-application and
  application-to-wire delegates for callers that prefer one expression. The invoker asks the narrow
  `IGrpcPrincipalFactory` for the principal already authenticated by the host, creates a new
  `DispatchScopeContext`, stores both boundary values, flows cancellation, and calls `HandlerInvoker`; it
  never resolves a raw handler or scans for service methods.
- `GrpcStreamHandlerInvoker.InvokeServerStreamingAsync<TRequest,TItem>` applies the same principal,
  dispatch-scope, and cancellation boundary to `StreamHandlerInvoker`. It returns the accepted
  `StreamHandlerInvocation<TItem>`, whose scope stays alive through one enumeration and must be disposed.
  Its convenience overload maps a wire request and writes explicitly mapped items to an
  `IServerStreamWriter<TWireItem>`. A failed startup `Result` translates before an item is written;
  exceptions during lazy enumeration propagate as the gRPC call's terminal fault because a started stream
  cannot become a different response.
- `GrpcAppErrorTranslator` maps `Validation`, `NotFound`, `Conflict`, `Forbidden`, `Unauthorized`,
  `BusinessRule`, and `Internal` to stable gRPC statuses. `RpcException.Status.Detail` receives the
  application message and the `elarion-error-kind` trailer carries a normalized lower-case kind. Unknown
  future kinds fail-safe as `Internal`.
- `AddElarionGrpcTransport(context => principal)` registers the invoker, principal factory, and default
  translator with `TryAdd` semantics. A reflection-free overload accepts an `IGrpcPrincipalFactory`
  instance. Neither overload registers or configures a gRPC host. Applications replace the translator by
  registering `IAppErrorTranslator<RpcException>` first.

The package depends outwardly on `Elarion`, `Elarion.Abstractions`, `Grpc.Core.Api`, and the
dependency-light `Microsoft.Extensions.DependencyInjection.Abstractions` contract package. It contains no
ASP.NET hosting dependency, generated protobuf contract, reflection mapper, or `HandlerTransports` member.
Authentication and generated-service registration remain host concerns.

Ship `Elarion.Grpc.AspNetCore` as the conventional composition layer for the standard grpc-dotnet host:

- `services.AddGrpc().AddElarion()` registers the neutral adapter and captures the principal from the
  call's already-authenticated `HttpContext.User`.
- `ServerCallContext.InvokeElarionAsync<TRequest,TResponse>(request)` resolves `GrpcHandlerInvoker` from that
  call's `HttpContext.RequestServices`, so a generated service override needs no injected framework helper or
  mapper delegates. Protobuf-to-application and application-to-protobuf mapping remain ordinary explicit code
  immediately around the dispatch. The mapper-delegate overload remains available for compact overrides.
- `ServerCallContext.InvokeElarionStreamAsync<TRequest,TItem>(request)` resolves
  `GrpcStreamHandlerInvoker` from the same request services. The generated service owns protobuf item
  mapping and uses `await using` around the returned invocation; a writer overload is available for a
  compact override.
- This package alone references `Grpc.AspNetCore.Server` and the ASP.NET Core shared framework. It adds no
  interceptor, middleware, service scanning, generated contract, or automatic mapping.

Using request services here is a deliberate adapter convention rather than ambient state in application
code: grpc-dotnet supplies the `ServerCallContext`, the invocation completes before the call ends, and
`GrpcHandlerInvoker` still owns the fresh Elarion dispatch scope. Non-ASP.NET hosts use the injected neutral
API instead.

Structured `ValidationErrorData` is not emitted as Google rich errors yet. A future addition needs a
stable protobuf detail contract; phase one preserves the machine-readable kind trailer without inventing
one.

## Consequences

- Unary and request-driven server-streaming gRPC services retain the same current-user seeding and decorated
  handler pipeline as other handler transports while keeping application DTOs independent of protobuf.
- The normal ASP.NET Core service override has no constructor or per-call principal plumbing; custom hosts
  retain the explicit injectable seam.
- Every method has deliberate mapping code around dispatch; this is accepted because mapping is the
  wire-contract seam.
- Client and duplex streaming remain out of scope. They require an inbound-message contract, whole-call
  scope/lifetime rules, backpressure, and a deliberate model for errors after either side has started.
  Server streaming is limited to the existing cold `IStreamHandler<TRequest,TItem>` seam; it does not
  automatically project ordered streams or client events.
