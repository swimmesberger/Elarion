using System.Collections.Concurrent;

namespace Elarion.WebPush;

/// <summary>
/// The process-local <see cref="IPushSubscriptionStore"/> default, for tests and single-node development:
/// subscriptions vanish on restart and are invisible to other nodes.
/// </summary>
public sealed class InMemoryPushSubscriptionStore : IPushSubscriptionStore {
    private readonly ConcurrentDictionary<string, PushSubscription> _subscriptions = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public ValueTask<bool> UpsertAsync(PushSubscription subscription, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(subscription);
        // Compare-and-swap, so the ownership check and the write are one step, as in the EF store's single
        // statement: a concurrent write between them makes TryAdd/TryUpdate fail and the check run again.
        while (true) {
            if (!_subscriptions.TryGetValue(subscription.Endpoint, out var existing)) {
                if (_subscriptions.TryAdd(subscription.Endpoint, subscription)) return ValueTask.FromResult(true);
                continue;
            }

            var sameOwner = existing.UserId == subscription.UserId;
            if (!sameOwner && existing.Auth != subscription.Auth) return ValueTask.FromResult(false);

            var updated = sameOwner ? subscription with { CreatedAt = existing.CreatedAt } : subscription;
            if (_subscriptions.TryUpdate(subscription.Endpoint, updated, existing)) return ValueTask.FromResult(true);
        }
    }

    /// <inheritdoc />
    public ValueTask<bool> RemoveAsync(string endpoint, string? userId = null,
        CancellationToken cancellationToken = default) {
        ArgumentException.ThrowIfNullOrEmpty(endpoint);
        if (userId is null) return ValueTask.FromResult(_subscriptions.TryRemove(endpoint, out _));

        // Remove only the exact owned entry, so a concurrent reassignment to another user survives.
        return ValueTask.FromResult(
            _subscriptions.TryGetValue(endpoint, out var existing)
            && existing.UserId == userId
            && _subscriptions.TryRemove(new KeyValuePair<string, PushSubscription>(endpoint, existing)));
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<PushSubscription>> ListByUsersAsync(IReadOnlyCollection<string> userIds,
        CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(userIds);
        var owners = userIds as IReadOnlySet<string> ?? new HashSet<string>(userIds, StringComparer.Ordinal);
        IReadOnlyList<PushSubscription> matches = _subscriptions.Values
            .Where(subscription => owners.Contains(subscription.UserId))
            .ToArray();
        return ValueTask.FromResult(matches);
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<string>> ListSubscribedUserIdsAsync(IReadOnlyCollection<string>? among = null,
        CancellationToken cancellationToken = default) {
        var owners = among is null ? null : among as IReadOnlySet<string> ?? new HashSet<string>(among, StringComparer.Ordinal);
        IReadOnlyList<string> ids = _subscriptions.Values
            .Select(subscription => subscription.UserId)
            .Where(userId => owners is null || owners.Contains(userId))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        return ValueTask.FromResult(ids);
    }
}
