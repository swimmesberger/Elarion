namespace Elarion.WebPush.EntityFrameworkCore;

/// <summary>
/// The persisted row backing <see cref="IPushSubscriptionStore"/>: one browser push subscription, unique by
/// <see cref="Endpoint"/>.
/// </summary>
public sealed class PushSubscriptionEntity {
    /// <summary>The row identity (client-assigned v7 GUID); stable across re-subscribes of the same endpoint.</summary>
    public Guid Id { get; init; }

    /// <summary>The push service endpoint URL (unique).</summary>
    public required string Endpoint { get; init; }

    /// <summary>The subscription's P-256 public key (base64url).</summary>
    public required string P256dh { get; set; }

    /// <summary>The subscription's authentication secret (base64url).</summary>
    public required string Auth { get; set; }

    /// <summary>The owning user.</summary>
    public required string UserId { get; set; }

    /// <summary>The subscribing browser's user agent.</summary>
    public string? UserAgent { get; set; }

    /// <summary>When the subscription was first stored for its current owner.</summary>
    public DateTimeOffset CreatedOnUtc { get; set; }

    /// <summary>When the browser last (re-)subscribed.</summary>
    public DateTimeOffset LastSeenOnUtc { get; set; }
}
