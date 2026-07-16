# ADR-0063: gRPC ships as a typed unary transport adapter

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

Ship `Elarion.Grpc`, a host-neutral package with a typed unary entry point:

- The injectable `GrpcHandlerInvoker.InvokeUnaryAsync` accepts the wire request, exact `ServerCallContext`,
  and explicit wire-to-application/application-to-wire delegates. The wire request plus one typed response
  lambda let C# infer all four types, so a generated override contains no framework generic list. The invoker
  asks the narrow `IGrpcPrincipalFactory` for the principal already authenticated by the host, creates a new
  `DispatchScopeContext`, stores both boundary values, flows cancellation, and calls `HandlerInvoker`; it
  never resolves a raw handler or scans for service methods.
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
- `ServerCallContext.InvokeElarionAsync(...)` resolves `GrpcHandlerInvoker` from that call's
  `HttpContext.RequestServices`, so a generated service override needs no injected framework helper. The
  request, context, and two explicit mapper lambdas remain visible at the wire boundary.
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

- Unary gRPC services retain the same current-user seeding and decorator pipeline as other handler
  transports while keeping application DTOs independent of protobuf.
- The normal ASP.NET Core service override has no constructor or per-call principal plumbing; custom hosts
  retain the explicit injectable seam.
- Every method has deliberate mapping code; this is accepted because mapping is the wire-contract seam.
- Streaming is not implemented. Server/client/duplex streaming requires a whole-call scope, upfront vs
  post-first-item failure semantics, trailer-based terminal errors, and cancellation through the entire
  stream. It follows the distinct stream-contract work already deferred by ADR-0044 rather than widening
  this unary adapter.
