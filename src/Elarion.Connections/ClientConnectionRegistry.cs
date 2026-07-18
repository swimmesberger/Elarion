using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;
using Elarion.Abstractions.Connections;
using Elarion.Connections.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Elarion.Connections;

/// <summary>
/// The node-local registry default: a concurrent index of this instance's live connections, doubling as the
/// lifecycle broker — observers are notified after the index mutation (a connect observer already sees the
/// connection in lookups, a disconnect observer no longer does), and an observer failure is logged, never
/// propagated, so one faulty observer can neither tear down a connection nor starve its peers.
/// </summary>
internal sealed class ClientConnectionRegistry(
    IEnumerable<IClientConnectionObserver> observers,
    ElarionConnectionsOptions options,
    ILogger<ClientConnectionRegistry>? logger = null) : IClientConnectionRegistry {
    private readonly IClientConnectionObserver[] _observers = [.. observers];
    private readonly ILogger<ClientConnectionRegistry> _logger =
        logger ?? NullLogger<ClientConnectionRegistry>.Instance;
    private static readonly AsyncLocal<NotificationScope?> CurrentNotification = new();
    private readonly ConcurrentDictionary<string, Registration> _connections = new(StringComparer.Ordinal);

    public async ValueTask RegisterAsync(IClientConnectionSink connection, CancellationToken ct = default) {
        ArgumentNullException.ThrowIfNull(connection);

        var state = connection.ConnectionState;
        var initial = state.Current;
        var normalized = NormalizeInitial(initial);
        var registration = new Registration(connection, state);
        if (!_connections.TryAdd(normalized.ConnectionId, registration)) {
            throw new InvalidOperationException(
                $"A connection with id '{normalized.ConnectionId}' is already registered. Connection ids are unique per node by contract — a duplicate registration is an adapter bug, not a race to tolerate.");
        }

        if (!state.TryRegister(initial, normalized)) {
            _connections.TryRemove(new KeyValuePair<string, Registration>(normalized.ConnectionId, registration));
            throw new InvalidOperationException(
                $"Connection adapter '{connection.GetType().FullName}' changed its identity snapshot before registration could normalize it.");
        }

        ConnectionTelemetry.RecordOpened(normalized.Transport);
        Task notification;
        lock (registration.Gate) {
            notification = registration.EnqueueNotification(
                () => NotifyConnectedAsync(connection, normalized.ConnectionId, ct).AsTask());
        }
        await notification;
    }

    public async ValueTask UnregisterAsync(string connectionId, CancellationToken ct = default) {
        ArgumentException.ThrowIfNullOrEmpty(connectionId);
        if (!_connections.TryGetValue(connectionId, out var registration)) {
            return;
        }

        ThrowIfReentrantLifecycleMutation(registration);
        ClientConnection final;
        Task notification;
        lock (registration.Gate) {
            registration.State.Unregister();
            if (!_connections.TryRemove(
                    new KeyValuePair<string, Registration>(connectionId, registration))) {
                return;
            }

            final = registration.Connection.Connection;
            notification = registration.EnqueueNotification(
                () => NotifyDisconnectedAsync(final, connectionId, ct).AsTask());
        }

        ConnectionTelemetry.RecordClosed(final.Transport);
        await notification;
    }

    public async ValueTask<ClientConnectionPromotionStatus> PromoteAsync(
        string connectionId,
        ClientConnectionIdentity identity,
        CancellationToken ct = default) {
        ArgumentException.ThrowIfNullOrEmpty(connectionId);
        ArgumentNullException.ThrowIfNull(identity);

        if (!_connections.TryGetValue(connectionId, out var registration)) {
            ConnectionTelemetry.RecordPromotion("unknown", ClientConnectionPromotionStatus.ConnectionNotFound);
            return ClientConnectionPromotionStatus.ConnectionNotFound;
        }

        var transport = registration.Connection.Connection.Transport;
        var status = await PromoteRegisteredAsync(registration, connectionId, identity, ct);
        ConnectionTelemetry.RecordPromotion(transport, status);
        return status;
    }

    private async ValueTask<ClientConnectionPromotionStatus> PromoteRegisteredAsync(
        Registration registration,
        string connectionId,
        ClientConnectionIdentity identity,
        CancellationToken ct) {
        ThrowIfReentrantLifecycleMutation(registration);
        ClientConnection previous;
        ClientConnection current;
        Task notification;
        lock (registration.Gate) {
            if (!_connections.TryGetValue(connectionId, out var live)
                || !ReferenceEquals(live, registration)) {
                return ClientConnectionPromotionStatus.ConnectionNotFound;
            }

            previous = registration.Connection.Connection;
            if (!CanPromote(previous)) {
                return ClientConnectionPromotionStatus.AlreadyAuthenticated;
            }

            current = CreatePromotedSnapshot(previous, identity);
            if (!registration.State.TryPromote(previous, current)) {
                return _connections.TryGetValue(connectionId, out live) && ReferenceEquals(live, registration)
                    ? ClientConnectionPromotionStatus.AlreadyAuthenticated
                    : ClientConnectionPromotionStatus.ConnectionNotFound;
            }

            notification = registration.EnqueueNotification(
                () => NotifyIdentityPromotedAsync(previous, current, connectionId, ct).AsTask());
        }

        await notification;
        return ClientConnectionPromotionStatus.Promoted;
    }

    public bool TryGet(string connectionId, [NotNullWhen(true)] out IClientConnectionSink? connection) {
        ArgumentException.ThrowIfNullOrEmpty(connectionId);
        if (_connections.TryGetValue(connectionId, out var registration)) {
            connection = registration.Connection;
            return true;
        }

        connection = null;
        return false;
    }

    public IReadOnlyList<IClientConnectionSink> GetForPrincipal(string principalId) {
        ArgumentException.ThrowIfNullOrEmpty(principalId);
        // O(connections) by design: the index is node-local and right-sized for the 1–10-node tier; a
        // principal index would only earn its complexity at a connection count where the seam gets
        // replaced anyway.
        return [.. _connections.Values
            .Select(static registration => registration.Connection)
            .Where(connection => string.Equals(connection.Connection.PrincipalId, principalId, StringComparison.Ordinal))];
    }

    public IReadOnlyCollection<IClientConnectionSink> Connections =>
        [.. _connections.Values.Select(static registration => registration.Connection)];

    private async ValueTask NotifyConnectedAsync(
        IClientConnectionSink connection,
        string connectionId,
        CancellationToken ct) {
        foreach (var observer in _observers) {
            try {
                await observer.OnConnectedAsync(connection, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                throw;
            }
            catch (Exception failure) {
                _logger.LogWarning(failure,
                    "Client-connection observer {Observer} failed on connect of {ConnectionId}.",
                    observer.GetType().Name, connectionId);
            }
        }
    }

    private async ValueTask NotifyDisconnectedAsync(ClientConnection connection, string connectionId, CancellationToken ct) {
        foreach (var observer in _observers) {
            try {
                await observer.OnDisconnectedAsync(connection, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                throw;
            }
            catch (Exception failure) {
                _logger.LogWarning(failure,
                    "Client-connection observer {Observer} failed on disconnect of {ConnectionId}.",
                    observer.GetType().Name, connectionId);
            }
        }
    }

    private async ValueTask NotifyIdentityPromotedAsync(
        ClientConnection previous,
        ClientConnection current,
        string connectionId,
        CancellationToken ct) {
        foreach (var observer in _observers) {
            try {
                await observer.OnIdentityPromotedAsync(previous, current, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                throw;
            }
            catch (Exception failure) {
                _logger.LogWarning(failure,
                    "Client-connection observer {Observer} failed on identity promotion of {ConnectionId}.",
                    observer.GetType().Name, connectionId);
            }
        }
    }

    private ClientConnection NormalizeInitial(ClientConnection connection) {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrEmpty(connection.ConnectionId);
        ArgumentException.ThrowIfNullOrEmpty(connection.Transport);
        ArgumentNullException.ThrowIfNull(connection.Principal);
        if (connection.IdentityRevision != 0) {
            throw new ArgumentException("Initial connection identity revision must be zero.", nameof(connection));
        }

        var authenticated = IsAuthenticated(connection.Principal);
        if (authenticated && string.IsNullOrWhiteSpace(connection.PrincipalId)) {
            throw new ArgumentException("Authenticated initial connections require a non-empty principal id.", nameof(connection));
        }

        if (!authenticated && !string.IsNullOrEmpty(connection.PrincipalId)) {
            throw new ArgumentException("Anonymous initial connections must not have a principal id.", nameof(connection));
        }

        return connection with {
            Principal = ClonePrincipal(connection.Principal),
            Metadata = NormalizeMetadata(connection.Metadata),
        };
    }

    private ClientConnection CreatePromotedSnapshot(ClientConnection previous, ClientConnectionIdentity identity) {
        ArgumentNullException.ThrowIfNull(identity.Principal);
        if (!IsAuthenticated(identity.Principal)) {
            throw new ArgumentException("Promoted identity must be authenticated.", nameof(identity));
        }

        if (string.IsNullOrWhiteSpace(identity.PrincipalId)) {
            throw new ArgumentException("Promoted identity requires a non-empty principal id.", nameof(identity));
        }

        return previous with {
            Principal = ClonePrincipal(identity.Principal),
            PrincipalId = identity.PrincipalId,
            IdentityRevision = 1,
            Metadata = NormalizeMetadata(identity.Metadata),
        };
    }

    private static bool CanPromote(ClientConnection connection) =>
        connection.IdentityRevision == 0
        && string.IsNullOrEmpty(connection.PrincipalId)
        && !IsAuthenticated(connection.Principal);

    private static bool IsAuthenticated(ClaimsPrincipal principal) =>
        principal.Identities.Any(static identity => identity.IsAuthenticated);

    private IReadOnlyDictionary<string, string> NormalizeMetadata(IReadOnlyDictionary<string, string> metadata) {
        ArgumentNullException.ThrowIfNull(metadata);
        var capacity = Math.Clamp(metadata.Count, 0, options.MaxIdentityMetadataEntries);
        var copy = new Dictionary<string, string>(capacity, StringComparer.Ordinal);
        foreach (var (key, value) in metadata) {
            if (key is null) {
                throw new ArgumentException("Identity metadata keys cannot be null.", nameof(metadata));
            }

            if (value is null) {
                throw new ArgumentException("Identity metadata values cannot be null.", nameof(metadata));
            }

            if (copy.Count == options.MaxIdentityMetadataEntries) {
                throw new ArgumentException(
                    $"Identity metadata cannot contain more than {options.MaxIdentityMetadataEntries} entries.",
                    nameof(metadata));
            }

            if (key.Length > options.MaxIdentityMetadataKeyLength) {
                throw new ArgumentException(
                    $"Identity metadata keys cannot exceed {options.MaxIdentityMetadataKeyLength} characters.",
                    nameof(metadata));
            }

            if (value.Length > options.MaxIdentityMetadataValueLength) {
                throw new ArgumentException(
                    $"Identity metadata values cannot exceed {options.MaxIdentityMetadataValueLength} characters.",
                    nameof(metadata));
            }

            try {
                copy.Add(key, value);
            }
            catch (ArgumentException exception) {
                throw new ArgumentException("Identity metadata contains duplicate ordinal keys.", nameof(metadata), exception);
            }
        }

        return copy.ToFrozenDictionary(StringComparer.Ordinal);
    }

    private ClaimsPrincipal ClonePrincipal(ClaimsPrincipal principal) {
        var identities = principal.Identities.ToArray();
        if (identities.Length > options.MaxPrincipalIdentities) {
            throw new ArgumentException(
                $"Connection principals cannot contain more than {options.MaxPrincipalIdentities} identities.",
                nameof(principal));
        }

        var visiting = new HashSet<ClaimsIdentity>(ReferenceEqualityComparer.Instance);
        var claimCount = 0;
        var clones = new List<ClaimsIdentity>(identities.Length);
        foreach (var identity in identities) {
            clones.Add(CloneIdentity(identity, visiting, depth: 0, ref claimCount));
        }

        return new ClaimsPrincipal(clones);
    }

    private ClaimsIdentity CloneIdentity(
        ClaimsIdentity identity,
        HashSet<ClaimsIdentity> visiting,
        int depth,
        ref int claimCount) {
        if (depth > options.MaxPrincipalActorDepth) {
            throw new ArgumentException(
                $"Connection principal actor depth cannot exceed {options.MaxPrincipalActorDepth}.",
                nameof(identity));
        }

        if (!visiting.Add(identity)) {
            throw new ArgumentException("Connection principals cannot contain a cyclic actor identity graph.", nameof(identity));
        }

        try {
            var claims = identity.Claims.ToArray();
            claimCount = checked(claimCount + claims.Length);
            if (claimCount > options.MaxPrincipalClaims) {
                throw new ArgumentException(
                    $"Connection principals cannot contain more than {options.MaxPrincipalClaims} claims.",
                    nameof(identity));
            }

            var clone = new ClaimsIdentity(
                claims.Select(CloneClaim),
                identity.AuthenticationType,
                identity.NameClaimType,
                identity.RoleClaimType) {
                Label = identity.Label,
                BootstrapContext = CloneBootstrapContext(identity.BootstrapContext),
            };
            if (identity.Actor is not null) {
                clone.Actor = CloneIdentity(identity.Actor, visiting, depth + 1, ref claimCount);
            }

            return clone;
        }
        finally {
            visiting.Remove(identity);
        }
    }

    private static object? CloneBootstrapContext(object? context) => context switch {
        null => null,
        string value => value,
        byte[] value => value.ToArray(),
        ICloneable value => value.Clone(),
        _ => throw new ArgumentException(
            "Connection principal bootstrap context must be a string, byte array, or cloneable value.",
            nameof(context)),
    };

    private static void ThrowIfReentrantLifecycleMutation(Registration registration) {
        if (CurrentNotification.Value is { IsActive: true } scope
            && ReferenceEquals(scope.Registration, registration)) {
            throw new InvalidOperationException(
                "A connection observer cannot synchronously promote or unregister the same connection from its lifecycle callback.");
        }
    }

    private static Claim CloneClaim(Claim claim) {
        var clone = new Claim(claim.Type, claim.Value, claim.ValueType, claim.Issuer, claim.OriginalIssuer);
        foreach (var (key, value) in claim.Properties) {
            clone.Properties.Add(key, value);
        }

        return clone;
    }

    private sealed class NotificationScope(Registration registration) {
        public Registration Registration { get; } = registration;
        public bool IsActive { get; set; } = true;
    }

    private sealed class Registration(
        IClientConnectionSink connection,
        ClientConnectionState state) {
        private Task _notificationTail = Task.CompletedTask;

        public IClientConnectionSink Connection { get; } = connection;
        public ClientConnectionState State { get; } = state;
        public Lock Gate { get; } = new();

        public Task EnqueueNotification(Func<Task> notify) {
            _notificationTail = NotifyAfterAsync(_notificationTail, notify);
            return _notificationTail;
        }

        private async Task NotifyAfterAsync(Task previous, Func<Task> notify) {
            try {
                await previous.ConfigureAwait(false);
            }
            catch {
                // Each lifecycle edge reports its own observer cancellation/failure to its caller. A prior
                // caller's exception must not suppress later ordered edges such as disconnect.
            }

            var scope = new NotificationScope(this);
            var prior = CurrentNotification.Value;
            CurrentNotification.Value = scope;
            try {
                await notify().ConfigureAwait(false);
            }
            finally {
                scope.IsActive = false;
                CurrentNotification.Value = prior;
            }
        }
    }
}
