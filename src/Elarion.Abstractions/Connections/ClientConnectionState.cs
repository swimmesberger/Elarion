namespace Elarion.Abstractions.Connections;

/// <summary>
/// The atomic source of immutable snapshots for one live connection. Adapters create it with their initial
/// snapshot and expose it through <see cref="IClientConnectionSink.ConnectionState"/>; application code reads
/// <see cref="Current"/>, while the registry alone can publish a normalized or promoted replacement.
/// </summary>
public sealed class ClientConnectionState {
    private readonly Lock _gate = new();
    private ClientConnection _current;
    private bool _isRegistered;

    /// <summary>Creates state for an adapter's initial connection snapshot.</summary>
    /// <param name="initial">The initial snapshot, normalized by the registry during registration.</param>
    public ClientConnectionState(ClientConnection initial) {
        ArgumentNullException.ThrowIfNull(initial);
        _current = initial;
    }

    /// <summary>The current immutable snapshot. Capture this property once at an operation boundary.</summary>
    public ClientConnection Current => Volatile.Read(ref _current);

    /// <summary>
    /// Whether the registry currently owns this connection lifecycle. Adapters and connection helpers use
    /// this to reject work that raced unregistration; only the registry changes it.
    /// </summary>
    public bool IsRegistered => Volatile.Read(ref _isRegistered);

    internal bool TryRegister(ClientConnection expected, ClientConnection normalized) {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(normalized);
        lock (_gate) {
            if (_isRegistered || !ReferenceEquals(_current, expected)) {
                return false;
            }

            _current = normalized;
            Volatile.Write(ref _isRegistered, true);
            return true;
        }
    }

    internal bool TryPromote(ClientConnection expected, ClientConnection updated) {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(updated);
        lock (_gate) {
            if (!_isRegistered || !ReferenceEquals(_current, expected)) {
                return false;
            }

            _current = updated;
            return true;
        }
    }

    internal bool IsCurrent(long identityRevision) {
        lock (_gate) {
            return _isRegistered && _current.IdentityRevision == identityRevision;
        }
    }

    internal void Unregister() {
        lock (_gate) {
            Volatile.Write(ref _isRegistered, false);
        }
    }
}
