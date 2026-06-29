using System.Diagnostics.CodeAnalysis;

namespace Elarion.Abstractions.Dispatch;

/// <summary>
/// A single named route in the transport-neutral handler dispatcher: a name bound to a handler, with the
/// request/response types a transport needs to (de)serialize, and a delegate that resolves the decorated
/// <see cref="IHandler{TRequest,TResponse}"/> and invokes it.
/// </summary>
/// <remarks>
/// The route carries no serialization or wire concern. A transport deserializes its wire payload into
/// <see cref="RequestType"/>, calls <see cref="InvokeAsync"/> with that instance, and maps the resulting
/// <see cref="Result{T}"/> (success value or <see cref="AppError"/>) onto its wire format. The same route is
/// reused by every name-routed transport (JSON-RPC, MCP, gRPC, a CLI, …).
/// </remarks>
public sealed record HandlerRoute(
    string Name,
    // A transport constructs an empty request via Activator.CreateInstance(RequestType) when no params are
    // supplied, so the parameterless constructor must survive trimming. Generated callers pass typeof(TRequest)
    // literals, which already satisfy this — the annotation only flows the requirement to the API surface.
    [property: DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)]
    Type RequestType,
    Type ResponseType,
    HandlerTransports Transports,
    Func<object, IServiceProvider, CancellationToken, ValueTask<Result<object>>> InvokeAsync);
