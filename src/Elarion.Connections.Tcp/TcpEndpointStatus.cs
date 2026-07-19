namespace Elarion.Connections.Tcp;

/// <summary>The direction a runtime-managed endpoint was applied as.</summary>
public enum TcpEndpointMode {
    /// <summary>Server-based: the endpoint listens and devices dial in.</summary>
    Listener,

    /// <summary>Client-based: the endpoint dials the device and maintains the link.</summary>
    Dialer
}

/// <summary>The advertised state of a runtime-managed endpoint.</summary>
public enum TcpEndpointState {
    /// <summary>Applied; the loop has not reported an outcome yet.</summary>
    Starting,

    /// <summary>The listener is bound and accepting.</summary>
    Listening,

    /// <summary>The dialer is between sessions — attempting to connect or backing off
    /// (<see cref="TcpEndpointStatus.Error"/> carries the last attempt's failure, when any).</summary>
    Dialing,

    /// <summary>The dialer's session is established.</summary>
    Connected,

    /// <summary>The endpoint failed permanently (e.g. the listen port could not be bound) and is not
    /// serving — <see cref="TcpEndpointStatus.Error"/> says why. Re-apply the binding to retry.</summary>
    Faulted
}

/// <summary>
/// One runtime-managed endpoint's advertised status — the operator-facing binding health an admin UI
/// queries (<c>TcpConnectionEndpoints.Statuses</c>) or observes (<c>TcpConnectionEndpoints.StatusChanged</c>,
/// typically projected onto a client event): which bindings are serving, which failed to bind, and why.
/// </summary>
public sealed record TcpEndpointStatus {
    /// <summary>The binding key the endpoint was applied under.</summary>
    public required string Name { get; init; }

    /// <summary>The direction the endpoint was applied as.</summary>
    public required TcpEndpointMode Mode { get; init; }

    /// <summary>The current state.</summary>
    public required TcpEndpointState State { get; init; }

    /// <summary>The current or last failure reason — non-null for <see cref="TcpEndpointState.Faulted"/>,
    /// and for <see cref="TcpEndpointState.Dialing"/> after a failed attempt.</summary>
    public string? Error { get; init; }

    /// <summary>When the endpoint entered this state.</summary>
    public required DateTimeOffset ChangedAt { get; init; }
}
