using Elarion.Abstractions.Connections;

namespace Elarion.Connections.Simulation;

/// <summary>
/// A connection observer whose lifecycle edges are awaitable — the deterministic replacement for polling
/// the registry in tests: <c>await observer.Connected.Task</c> after driving a connect,
/// <c>await observer.Disconnected.Task</c> after a teardown, <see cref="Reset"/> between rounds
/// (reconnect scenarios). Register it alongside your own observers.
/// </summary>
public sealed class AwaitableConnectionObserver : IClientConnectionObserver {
    private TaskCompletionSource<IClientConnectionSink> _connected = NewSink();
    private TaskCompletionSource<ClientConnectionIdentityPromotion> _promoted = NewPromotion();
    private TaskCompletionSource<ClientConnection> _disconnected = NewConnection();

    /// <summary>Completes with the sink of the next (or already observed) connect.</summary>
    public TaskCompletionSource<IClientConnectionSink> Connected => _connected;

    /// <summary>Completes with the next (or already observed) identity promotion.</summary>
    public TaskCompletionSource<ClientConnectionIdentityPromotion> Promoted => _promoted;

    /// <summary>Completes with the identity of the next (or already observed) disconnect.</summary>
    public TaskCompletionSource<ClientConnection> Disconnected => _disconnected;

    /// <summary>Arms fresh completions for the next connect, promotion, and disconnect lifecycle edges.</summary>
    public void Reset() {
        _connected = NewSink();
        _promoted = NewPromotion();
        _disconnected = NewConnection();
    }

    /// <inheritdoc />
    public ValueTask OnConnectedAsync(IClientConnectionSink connection, CancellationToken ct = default) {
        _connected.TrySetResult(connection);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask OnIdentityPromotedAsync(
        ClientConnection previous,
        ClientConnection current,
        CancellationToken ct = default) {
        _promoted.TrySetResult(new ClientConnectionIdentityPromotion {
            Previous = previous,
            Current = current
        });
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask OnDisconnectedAsync(ClientConnection connection, CancellationToken ct = default) {
        _disconnected.TrySetResult(connection);
        return ValueTask.CompletedTask;
    }

    private static TaskCompletionSource<IClientConnectionSink> NewSink() {
        return new TaskCompletionSource<IClientConnectionSink>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private static TaskCompletionSource<ClientConnectionIdentityPromotion> NewPromotion() {
        return new TaskCompletionSource<ClientConnectionIdentityPromotion>(TaskCreationOptions
            .RunContinuationsAsynchronously);
    }

    private static TaskCompletionSource<ClientConnection> NewConnection() {
        return new TaskCompletionSource<ClientConnection>(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

/// <summary>The immutable before/after snapshots of one observed identity promotion.</summary>
public sealed record ClientConnectionIdentityPromotion {
    /// <summary>The anonymous identity snapshot that was replaced.</summary>
    public required ClientConnection Previous { get; init; }

    /// <summary>The authenticated identity snapshot committed by the registry.</summary>
    public required ClientConnection Current { get; init; }
}
