namespace Elarion.Abstractions.Connections;

/// <summary>
/// The per-connection outbound port, implemented by the transport adapter: push a named payload to this one
/// client (<see cref="SendAsync"/>), or address it and await its answer (<see cref="InvokeAsync"/> —
/// server→client RPC). Framing, correlation, and encoding are adapter-owned; the seam defines only <i>what</i>
/// travels: a wire name plus a typed payload.
/// </summary>
/// <remarks>
/// <para>
/// The sink is deliberately <b>node-local</b>: you can only talk to a client you hold. Reaching a client
/// held elsewhere composes from the shipped pieces (a connection-owning single-homed actor plus the
/// role-holder proxy), never from a foundation-level connection directory.
/// </para>
/// <para>
/// Server→client <b>facts</b> should not go through raw <see cref="SendAsync"/> — client events remain the
/// one semantic model for facts (topic catalog, subscribe-time authorization, cross-node fan-out), and a
/// connection adapter is just another delivery leg for them. The sink is for conversation-shaped traffic:
/// replies, adapter control frames, and RPC into the client.
/// </para>
/// <para>
/// Encoding is adapter-owned. JSON adapters serialize through the canonical accessor
/// (<c>IElarionJsonSerialization</c>), so payload types must be reachable from a registered
/// <see cref="System.Text.Json.Serialization.JsonSerializerContext"/>; binary adapters encode the same
/// contracts in their own framing.
/// </para>
/// </remarks>
public interface IClientConnectionSink {
    /// <summary>The identity of the connection this sink writes to.</summary>
    ClientConnection Connection { get; }

    /// <summary>
    /// Pushes <paramref name="payload"/> to the client as <paramref name="name"/>, fire-and-forget:
    /// <b>at-most-once</b>, no acknowledgement, no redelivery. If the client is gone the send may surface
    /// <see cref="ClientConnectionClosedException"/> or complete as a no-op, depending on when the adapter
    /// learns of the disconnect — callers must not treat completion as proof of delivery.
    /// </summary>
    /// <typeparam name="TPayload">The payload contract type.</typeparam>
    /// <param name="name">The client-side operation name (wire name; the client routes on it).</param>
    /// <param name="payload">The payload instance.</param>
    /// <param name="ct">A cancellation token observed while handing the frame to the transport.</param>
    ValueTask SendAsync<TPayload>(string name, TPayload payload, CancellationToken ct = default)
        where TPayload : class;

    /// <summary>
    /// Invokes <paramref name="name"/> on the client with <paramref name="request"/> and awaits its reply.
    /// Completes with the client's response, or faults with <see cref="ClientConnectionClosedException"/>
    /// (the connection ended first), <see cref="TimeoutException"/> (no reply within
    /// <see cref="ClientInvokeOptions.Timeout"/> or the adapter default), or
    /// <see cref="OperationCanceledException"/> — <b>never silently</b>. The invoke is at-most-once: a fault
    /// leaves unknown whether the client observed the call, so anything the client does in response must be
    /// safe to re-request.
    /// </summary>
    /// <typeparam name="TRequest">The request contract type.</typeparam>
    /// <typeparam name="TResponse">The expected response contract type.</typeparam>
    /// <param name="name">The client-side operation name (wire name).</param>
    /// <param name="request">The request payload.</param>
    /// <param name="options">Per-call options; <see langword="null"/> uses adapter defaults.</param>
    /// <param name="ct">A cancellation token; cancelling abandons the wait, not the client's execution.</param>
    ValueTask<TResponse> InvokeAsync<TRequest, TResponse>(
        string name,
        TRequest request,
        ClientInvokeOptions? options = null,
        CancellationToken ct = default)
        where TRequest : class;
}
