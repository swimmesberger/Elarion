namespace Elarion.WebPush;

/// <summary>
/// One browser's push subscription, owned by one user. The <see cref="Endpoint"/> is the identity: a
/// browser profile has one per application server key, so the same device re-subscribing under another
/// account reassigns the row instead of creating a second one.
/// </summary>
public sealed record PushSubscription {
    /// <summary>The push service URL messages are POSTed to. Treat it as a secret-ish capability; do not log it.</summary>
    public required string Endpoint { get; init; }

    /// <summary>The subscription's P-256 public key (base64url, <c>keys.p256dh</c>).</summary>
    public required string P256dh { get; init; }

    /// <summary>The subscription's 16-byte authentication secret (base64url, <c>keys.auth</c>).</summary>
    public required string Auth { get; init; }

    /// <summary>The owning user (<c>ICurrentUser.UserId</c> at subscribe time).</summary>
    public required string UserId { get; init; }

    /// <summary>The subscribing browser's <c>User-Agent</c>, for a "your devices" list; truncated to 512 characters.</summary>
    public string? UserAgent { get; init; }

    /// <summary>When the subscription was first stored for its current owner.</summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>When the browser last (re-)subscribed — refreshed by every app start that calls <c>refreshOnStart</c>.</summary>
    public DateTimeOffset LastSeenAt { get; init; }
}

/// <summary>
/// Stores push subscriptions keyed by endpoint. The default is <see cref="InMemoryPushSubscriptionStore"/>;
/// <c>Elarion.WebPush.EntityFrameworkCore</c> provides the durable <c>elarion_push_subscriptions</c> table.
/// </summary>
public interface IPushSubscriptionStore {
    /// <summary>
    /// Inserts the subscription, or updates the row with the same <see cref="PushSubscription.Endpoint"/>:
    /// keys, owner, user agent, and <see cref="PushSubscription.LastSeenAt"/> are overwritten;
    /// <see cref="PushSubscription.CreatedAt"/> is kept unless the owner changed.
    /// </summary>
    /// <param name="subscription">The subscription, with both timestamps set to "now".</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    ValueTask UpsertAsync(PushSubscription subscription, CancellationToken cancellationToken = default);

    /// <summary>Deletes the subscription with <paramref name="endpoint"/>.</summary>
    /// <param name="endpoint">The subscription endpoint.</param>
    /// <param name="userId">
    /// When set, deletes only if that user owns the subscription — the check a user-initiated unsubscribe
    /// needs. <see langword="null"/> deletes regardless of owner (dead-subscription cleanup).
    /// </param>
    /// <param name="cancellationToken">Cancels the delete.</param>
    /// <returns>Whether a row was deleted.</returns>
    ValueTask<bool> RemoveAsync(string endpoint, string? userId = null, CancellationToken cancellationToken = default);

    /// <summary>Every subscription owned by any of <paramref name="userIds"/>.</summary>
    /// <param name="userIds">The owners.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    ValueTask<IReadOnlyList<PushSubscription>> ListByUsersAsync(IReadOnlyCollection<string> userIds,
        CancellationToken cancellationToken = default);
}
