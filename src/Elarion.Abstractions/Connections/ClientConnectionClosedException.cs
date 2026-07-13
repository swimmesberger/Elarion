namespace Elarion.Abstractions.Connections;

/// <summary>
/// Thrown by <see cref="IClientConnectionSink"/> operations when the connection ended before or during the
/// operation. For an <see cref="IClientConnectionSink.InvokeAsync"/> this is the disconnect fault of its
/// at-most-once contract: it leaves unknown whether the client observed the call.
/// </summary>
public sealed class ClientConnectionClosedException : Exception {
    /// <summary>The id of the connection that was closed.</summary>
    public string ConnectionId { get; }

    /// <summary>Creates the fault for the closed connection <paramref name="connectionId"/>.</summary>
    public ClientConnectionClosedException(string connectionId)
        : base($"Client connection '{connectionId}' is closed.") {
        ConnectionId = connectionId;
    }

    /// <summary>Creates the fault with the transport-level cause.</summary>
    public ClientConnectionClosedException(string connectionId, Exception innerException)
        : base($"Client connection '{connectionId}' is closed.", innerException) {
        ConnectionId = connectionId;
    }
}
