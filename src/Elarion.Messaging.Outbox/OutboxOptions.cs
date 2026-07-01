using System.Text.Json;

namespace Elarion.Messaging.Outbox;

/// <summary>
/// Configures the EF Core transactional outbox and its background delivery worker.
/// </summary>
public sealed class OutboxOptions
{
    /// <summary>How long the delivery worker waits between polls when the outbox is idle. Defaults to 1 second.</summary>
    /// <remarks>
    /// When a poll returns a full batch the worker keeps draining without waiting, so this bounds idle latency only.
    /// With the partial pending index an empty poll is a near-instant index probe, so a low interval is cheap.
    /// </remarks>
    public TimeSpan PollingInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>The maximum number of messages claimed per poll. Defaults to 100.</summary>
    public int BatchSize { get; set; } = 100;

    /// <summary>
    /// The maximum number of delivery attempts before a message is left for inspection and no longer retried.
    /// Defaults to 10.
    /// </summary>
    public int MaxDeliveryAttempts { get; set; } = 10;

    /// <summary>
    /// How long a claimed message stays leased to one worker before another may reclaim it. Defaults to 2 minutes.
    /// </summary>
    /// <remarks>
    /// A crashed worker's in-flight messages are retried once their lease expires, so this also bounds crash-recovery
    /// latency. In multi-instance deployments keep it comfortably above the time to deliver a full
    /// <see cref="BatchSize"/> batch, or a slow batch's tail can be reclaimed and redelivered.
    /// </remarks>
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// The base delay before a failed message becomes visible for its next attempt. Defaults to 5 seconds.
    /// </summary>
    /// <remarks>
    /// A failed message is made invisible for an exponentially growing delay derived from its attempt count
    /// (<c>BaseRetryDelay × 2^(attempts-1)</c>, capped at <see cref="MaxRetryDelay"/>), so a poison message no longer
    /// re-enters the front of every claim batch at full poll frequency — head-of-line blocking is avoided and retries
    /// back off. Set to <see cref="TimeSpan.Zero"/> to retry immediately on the next poll (the pre-backoff behavior).
    /// </remarks>
    public TimeSpan BaseRetryDelay { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>The ceiling on the exponential retry backoff computed from <see cref="BaseRetryDelay"/>. Defaults to 1 hour.</summary>
    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// How long delivered messages are retained before the worker purges them, or <c>null</c> to keep them forever.
    /// Defaults to 7 days.
    /// </summary>
    public TimeSpan? RetentionPeriod { get; set; } = TimeSpan.FromDays(7);

    /// <summary>
    /// Overrides the serializer used for event payloads. Defaults to <c>null</c> — the canonical Elarion JSON
    /// options (<c>IElarionJsonSerialization</c>) are used, so event DTOs resolve through the app's source-generated
    /// contexts (trim/AOT-safe). Set a non-null value only to serialize the outbox differently from the rest of the app.
    /// </summary>
    public JsonSerializerOptions? SerializerOptions { get; set; }
}
