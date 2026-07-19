namespace Elarion.Connections.Tcp;

/// <summary>
/// The one owner of a TCP connection's close transition: <c>Open → Closing → Closed</c>. Every close
/// initiator — peer EOF, explicit <see cref="TcpClientConnection.CloseAsync"/>, host/endpoint shutdown,
/// TLS/framing/codec/write failures, endpoint reconfiguration — funnels through
/// <see cref="TryBeginClose"/>, so the first reason wins and receive cancellation, writer drain/abort,
/// raw-transport disposal, and terminal completion each happen exactly once regardless of how many
/// initiators race.
/// </summary>
internal sealed class TcpConnectionLifetime : IDisposable {
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _receiveCts;
    private readonly IDisposable _transport;
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TcpOutboundWriter? _writer;
    private Exception? _closeReason;
    private bool _closeRequested;
    private bool _forced;
    private bool _transportDisposed;

    public TcpConnectionLifetime(IDisposable transport, CancellationToken hostToken) {
        _transport = transport;
        _receiveCts = CancellationTokenSource.CreateLinkedTokenSource(hostToken);
    }

    /// <summary>Cancelled when close begins (or the host token fires) — the receive loop's token.</summary>
    public CancellationToken ReceiveToken => _receiveCts.Token;

    /// <summary>Resolves when the runner has fully torn the connection down (state <c>Closed</c>).</summary>
    public Task Completion => _completion.Task;

    public bool CloseRequested {
        get {
            lock (_gate) {
                return _closeRequested;
            }
        }
    }

    /// <summary>The first close initiator's reason; <see langword="null"/> for a clean close.</summary>
    public Exception? CloseReason {
        get {
            lock (_gate) {
                return _closeReason;
            }
        }
    }

    /// <summary>Whether the close was forced (raw transport aborted) rather than drained gracefully.</summary>
    public bool WasForced {
        get {
            lock (_gate) {
                return _forced;
            }
        }
    }

    /// <summary>Attaches the outbound writer once it exists (after the framed handshake).</summary>
    public void AttachWriter(TcpOutboundWriter writer) {
        _writer = writer;
    }

    /// <summary>Records the first close reason and cancels the receive loop. Later calls are no-ops, so
    /// competing initiators cannot overwrite the reason.</summary>
    public bool TryBeginClose(Exception? reason) {
        lock (_gate) {
            if (_closeRequested) return false;

            _closeRequested = true;
            _closeReason = reason;
        }

        CancelReceive();
        return true;
    }

    /// <summary>
    /// Requests a graceful close: no new outbound sends are admitted, already-admitted sends drain, the
    /// receive loop stops, and the runner performs the once-only teardown (codec close, unregister,
    /// disposal). Idempotent.
    /// </summary>
    public void RequestGracefulClose(Exception? reason = null) {
        TryBeginClose(reason);
        _writer?.BeginGracefulClose();
    }

    /// <summary>
    /// Forces the connection down: pending sends fault, the raw transport is disposed so blocked reads and
    /// writes fail immediately, and the runner completes its teardown. Idempotent; safe to call after a
    /// graceful close that is not finishing within its grace period.
    /// </summary>
    public void Abort(Exception? reason) {
        TryBeginClose(reason);
        lock (_gate) {
            if (_forced) return;

            _forced = true;
        }

        CancelReceive();
        _writer?.Abort(reason);
        DisposeTransport();
    }

    /// <summary>Disposes the raw transport exactly once (also the normal end-of-runner disposal path).</summary>
    public void DisposeTransport() {
        lock (_gate) {
            if (_transportDisposed) return;

            _transportDisposed = true;
        }

        try {
            _transport.Dispose();
        }
        catch (Exception) {
            // Disposing a dying socket is best-effort; teardown continues regardless.
        }
    }

    /// <summary>Marks the terminal state: teardown finished, the runner is about to return.</summary>
    public void MarkClosed() {
        _completion.TrySetResult();
    }

    private void CancelReceive() {
        try {
            _receiveCts.Cancel();
        }
        catch (ObjectDisposedException) {
            // A late initiator raced runner completion — the connection is already down.
        }
    }

    public void Dispose() {
        _receiveCts.Dispose();
    }
}
