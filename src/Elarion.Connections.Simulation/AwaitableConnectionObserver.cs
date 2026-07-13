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
    private TaskCompletionSource<ClientConnection> _disconnected = NewConnection();

    /// <summary>Completes with the sink of the next (or already observed) connect.</summary>
    public TaskCompletionSource<IClientConnectionSink> Connected => _connected;

    /// <summary>Completes with the identity of the next (or already observed) disconnect.</summary>
    public TaskCompletionSource<ClientConnection> Disconnected => _disconnected;

    /// <summary>Arms fresh completions for the next connect/disconnect pair.</summary>
    public void Reset() {
        _connected = NewSink();
        _disconnected = NewConnection();
    }

    /// <inheritdoc />
    public ValueTask OnConnectedAsync(IClientConnectionSink connection, CancellationToken ct = default) {
        _connected.TrySetResult(connection);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask OnDisconnectedAsync(ClientConnection connection, CancellationToken ct = default) {
        _disconnected.TrySetResult(connection);
        return ValueTask.CompletedTask;
    }

    private static TaskCompletionSource<IClientConnectionSink> NewSink() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static TaskCompletionSource<ClientConnection> NewConnection() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
