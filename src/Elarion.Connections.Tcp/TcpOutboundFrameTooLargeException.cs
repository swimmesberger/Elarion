namespace Elarion.Connections.Tcp;

/// <summary>
/// The application payload produced a framed TCP message larger than the connection's configured
/// <see cref="ElarionTcpConnectionOptions.MaxOutboundFrameBytes"/> limit.
/// </summary>
/// <remarks>
/// The adapter rejects the complete frame before writing any of it to the transport. The connection remains
/// usable; callers may send a smaller frame or move bulk data to the staged-blob tier.
/// </remarks>
public sealed class TcpOutboundFrameTooLargeException : InvalidOperationException {
    /// <summary>Creates the outbound frame-size failure.</summary>
    public TcpOutboundFrameTooLargeException()
        : base("The framed outbound message exceeds the configured maximum frame size.") {
    }
}
