namespace Elarion.Connections.Tcp;

/// <summary>
/// Thrown by a send when the connection's bounded outbound queue is at
/// <see cref="ElarionTcpConnectionOptions.MaxPendingSends"/> capacity (in-progress work included). The
/// frame was <b>not</b> admitted, framed, or partially written — the connection stays healthy and the
/// caller decides whether to retry, shed, or treat saturation as backpressure. Conversation/RPC frames are
/// never silently dropped: saturation is always this deterministic fault.
/// </summary>
public sealed class TcpSendQueueFullException(string connectionId, int capacity)
    : Exception(
        $"Connection '{connectionId}' has {capacity} outbound sends pending — the send was rejected, not queued.") {
    /// <summary>The id of the saturated connection.</summary>
    public string ConnectionId { get; } = connectionId;

    /// <summary>The configured outbound capacity that was exhausted.</summary>
    public int Capacity { get; } = capacity;
}
