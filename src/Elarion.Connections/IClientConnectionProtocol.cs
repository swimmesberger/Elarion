using Elarion.Abstractions.Connections;

namespace Elarion.Connections;

/// <summary>
/// The app-owned codec for one connection, transport-neutral: adapters deliver <b>complete inbound
/// messages, sequentially, in receive order</b> (the adapter's single receive loop is the per-connection
/// ordering guarantee), and the neutral sink's outbound legs delegate here so the wire encoding stays the
/// codec's decision. The same codec runs over any message-delivering adapter — WebSocket frames, a
/// length-prefixed or line-delimited TCP stream (the adapter owns the framing that turns a byte stream into
/// messages), or a datagram transport.
/// </summary>
/// <remarks>
/// A proprietary device codec implements only the inbound members and sends raw frames via its adapter's
/// concrete connection type (e.g. the WebSocket adapter's <c>SendTextAsync</c>/<c>SendBinaryAsync</c>); the
/// neutral legs keep their fail-loud defaults. An exception thrown from any member ends the connection (the
/// adapter logs and closes) — a codec that cannot parse a message is a protocol violation, not something to
/// limp past. Created once per connection; instances may hold per-connection state (parser buffers, sequence
/// counters, the pending-request map an <see cref="InvokeAsync"/> implementation correlates on).
/// </remarks>
public interface IClientConnectionProtocol {
    /// <summary>Handles one complete inbound text message.</summary>
    /// <param name="message">The reassembled message.</param>
    /// <param name="ct">The connection's lifetime token.</param>
    ValueTask OnTextAsync(string message, CancellationToken ct) =>
        throw new NotSupportedException("This protocol does not accept text messages.");

    /// <summary>Handles one complete inbound binary message. The memory is only valid for the duration of
    /// the call — copy it if the codec defers work.</summary>
    /// <param name="message">The reassembled message.</param>
    /// <param name="ct">The connection's lifetime token.</param>
    ValueTask OnBinaryAsync(ReadOnlyMemory<byte> message, CancellationToken ct) =>
        throw new NotSupportedException("This protocol does not accept binary messages.");

    /// <summary>
    /// Called when no inbound message arrived within the adapter's configured idle window (and again per
    /// elapsed window while the connection stays idle) — the mounting point for protocol-level keepalives:
    /// send the poll/heartbeat frame from here, or throw to end a connection you consider dead. Never
    /// called unless the adapter's idle option is set; the default is a no-op.
    /// </summary>
    /// <param name="ct">The connection's lifetime token.</param>
    ValueTask OnIdleAsync(CancellationToken ct) => ValueTask.CompletedTask;

    /// <summary>
    /// Called once when the connection ends, after the receive loop has exited and before the connection is
    /// unregistered — the mounting point for codec teardown: fault the pending-invoke correlation
    /// (<see cref="ConnectionPendingRequests{TKey, TResponse}.FailAll"/>), complete the conversation inbox
    /// (<see cref="ConnectionInbox{TMessage}.Complete"/>), release per-connection resources. Without it a
    /// pending <see cref="InvokeAsync"/> would hang forever. The adapter failure-isolates the call — a
    /// throwing implementation is logged and never breaks teardown. The default is a no-op.
    /// </summary>
    /// <param name="connection">The identity of the connection that ended.</param>
    /// <param name="reason">The terminating exception, or <see langword="null"/> for a clean close (the
    /// peer ended the connection in an orderly way).</param>
    /// <param name="ct">A teardown-scoped token — never the connection's lifetime token, which is already
    /// cancelled on shutdown paths.</param>
    ValueTask OnClosedAsync(ClientConnection connection, Exception? reason, CancellationToken ct) =>
        ValueTask.CompletedTask;

    /// <summary>The codec behind <see cref="IClientConnectionSink.SendAsync"/> — encode and send a named
    /// payload. Codecs without a named-payload concept keep the fail-loud default.</summary>
    ValueTask SendAsync<TPayload>(string name, TPayload payload, CancellationToken ct) where TPayload : class =>
        throw new NotSupportedException("This protocol does not support named payload sends.");

    /// <summary>The codec behind <see cref="IClientConnectionSink.InvokeAsync"/> — encode, correlate, and
    /// await the client's reply, honoring <see cref="ClientInvokeOptions.Timeout"/>. Codecs without
    /// request/reply keep the fail-loud default.</summary>
    ValueTask<TResponse> InvokeAsync<TRequest, TResponse>(
        string name, TRequest request, ClientInvokeOptions? options, CancellationToken ct) where TRequest : class =>
        throw new NotSupportedException("This protocol does not support client invocation.");
}
