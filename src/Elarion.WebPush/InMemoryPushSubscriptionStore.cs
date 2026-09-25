using System.Collections.Concurrent;

namespace Elarion.WebPush;

/// <summary>
/// The process-local <see cref="IPushSubscriptionStore"/> default, for tests and single-node development:
/// subscriptions vanish on restart and are invisible to other nodes.
/// </summary>
public sealed class InMemoryPushSubscriptionStore : IPushSubscriptionStore {
    private readonly ConcurrentDictionary<string, PushSubscription> _subscriptions = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public ValueTask UpsertAsync(PushSubscription subscription, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(subscription);
        _subscriptions.AddOrUpdate(
            subscription.Endpoint,
            static (_, added) => added,
            static (_, existing, added) => existing.UserId == added.UserId
                ? added with { CreatedAt = existing.CreatedAt }
                : added,
            subscription);
        return ValueTask.CompletedTask;
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
}
