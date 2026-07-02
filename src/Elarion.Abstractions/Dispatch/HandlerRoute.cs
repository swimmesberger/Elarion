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
    // A transport deserializes its wire payload into RequestType through the configured (source-gen) JsonTypeInfo,
    // treating omitted params as an empty object — so an empty request needs no reflected parameterless
    // constructor and this type carries no trimming/AOT requirement on RequestType.
    Type RequestType,
    Type ResponseType,
    HandlerTransports Transports,
    Func<object, IServiceProvider, CancellationToken, ValueTask<Result<object>>> InvokeAsync,
    // Whether the handler is [Idempotent] — carried as route metadata (like Transports) so the exported schema
    // can advertise it and a generated client can attach an idempotency key by default. Not used for routing.
    bool Idempotent = false);
