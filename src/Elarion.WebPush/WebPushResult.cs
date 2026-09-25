namespace Elarion.WebPush;

/// <summary>
/// The outcome of one <see cref="IWebPushSender"/> fan-out. Delivery means the push service accepted the
/// message — not that the device displayed it; Web Push has no end-to-end receipt.
/// </summary>
public sealed record WebPushResult {
    /// <summary>A fan-out that found no subscriptions to send to.</summary>
    public static readonly WebPushResult Empty = new();

    /// <summary>How many subscriptions a send was attempted for.</summary>
    public int Attempted { get; init; }

    /// <summary>How many the push service accepted.</summary>
    public int Delivered { get; init; }

    /// <summary>
    /// How many were deleted from the store because they can never be delivered to: the push service
    /// reported them gone (404/410), their keys were malformed, or their endpoint host is not allowed.
    /// </summary>
    public int Removed { get; init; }

    /// <summary>
    /// How many failed transiently (network error, timeout, 429/5xx, or another rejection); those
    /// subscriptions are kept.
    /// </summary>
    public int Failed { get; init; }
}
