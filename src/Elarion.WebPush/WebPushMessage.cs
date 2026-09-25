namespace Elarion.WebPush;

/// <summary>
/// A notification to deliver through Web Push. Serialized as the <c>{title, body, url, tag}</c> JSON
/// payload the <c>@swimmesberger/elarion-webpush</c> service-worker module displays; recipients,
/// triggers, and the text itself stay application concerns.
/// </summary>
/// <example>
/// <code>
/// await sender.SendToUsersAsync([ownerId], new WebPushMessage {
///     Title = "Deploy failed",
///     Body = "api@4f2c1e failed its health check.",
///     Url = "/deploys/4f2c1e",
///     Tag = "deploy-4f2c1e",
///     Urgency = WebPushUrgency.High,
/// }, ct);
/// </code>
/// </example>
public sealed record WebPushMessage {
    /// <summary>The notification title.</summary>
    public required string Title { get; init; }

    /// <summary>The notification body text.</summary>
    public required string Body { get; init; }

    /// <summary>
    /// Where a click on the notification navigates (absolute or app-relative). The service worker focuses
    /// an open client at that URL or opens a new one.
    /// </summary>
    public string? Url { get; init; }

    /// <summary>
    /// A replacement key. The browser replaces a displayed notification with the same tag, and the push
    /// service replaces an undelivered message with the same tag (it is sent as the RFC 8030
    /// <c>Topic</c> header — verbatim when it is at most 32 URL-safe base64 characters, otherwise as a
    /// stable hash), so a burst of updates for one thing reaches the device as its latest state.
    /// </summary>
    public string? Tag { get; init; }

    /// <summary>How urgently the push service should deliver (RFC 8030 <c>Urgency</c>); affects battery use.</summary>
    public WebPushUrgency Urgency { get; init; } = WebPushUrgency.Normal;

    /// <summary>
    /// How long the push service keeps the message for an offline device (RFC 8030 <c>TTL</c>);
    /// <see langword="null"/> uses <see cref="WebPushOptions.DefaultTimeToLive"/>. <see cref="TimeSpan.Zero"/>
    /// means "deliver now or drop".
    /// </summary>
    public TimeSpan? TimeToLive { get; init; }
}

/// <summary>The RFC 8030 <c>Urgency</c> of a push message.</summary>
public enum WebPushUrgency {
    /// <summary>Deliver only when the device is on power and Wi-Fi (<c>very-low</c>).</summary>
    VeryLow,

    /// <summary>Deliver when the device is on power or Wi-Fi (<c>low</c>).</summary>
    Low,

    /// <summary>Deliver unless the device is in a low-battery state (<c>normal</c>, the default).</summary>
    Normal,

    /// <summary>Deliver immediately, even on low battery (<c>high</c>) — reserve for time-critical alerts.</summary>
    High
}
