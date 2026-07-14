using System.Text.Json;

namespace Elarion.Messaging.Outbox;

/// <summary>
/// Configures the EF Core transactional outbox and its background delivery worker.
/// </summary>
public sealed class OutboxOptions
{
    /// <summary>
    /// Whether this instance runs the background delivery worker (<c>OutboxDeliveryService</c>).
    /// Defaults to <see langword="true"/>.
    /// </summary>
    /// <remarks>
    /// Set to <see langword="false"/> on instances that should only <em>publish</em> to the outbox. In a
    /// heterogeneous topology — for example web nodes with a feature module disabled plus a worker-role node
    /// hosting that module's consumers (or its actors) — a node whose delivery loop claims a message whose
    /// consumers are not registered locally parks it as unresolvable. Run the worker only on the instance(s)
    /// that register <em>all</em> integration consumers; publisher-only nodes keep the bus and storage but
    /// skip the worker entirely.
    /// </remarks>
    public bool RunDeliveryWorker { get; set; } = true;

    /// <summary>
    /// Optional per-cycle gate for the delivery worker: consulted before each claim; when it returns
    /// <see langword="false"/> the cycle is skipped (no messages are claimed) and the worker idles
    /// until the next tick. <see langword="null"/> (default) always delivers.
    /// </summary>
    /// <remarks>
    /// The <em>dynamic</em> sibling of <see cref="RunDeliveryWorker"/>: use it when whether this
    /// instance delivers changes at runtime — the canonical case is following the actor home lease
    /// (ADR-0048) in a homogeneous deployment, so integration events (and the single-homed actors
    /// they feed) are delivered on exactly the lease-holding instance:
    /// <code>o.DeliveryGate = (sp, _) => ValueTask.FromResult(sp.GetRequiredService&lt;IActorHomeLease&gt;().IsHeld);</code>
    /// The service provider is the worker's poll scope. Messages published while the gate is closed
    /// simply wait in the outbox for whichever instance's gate opens.
    /// </remarks>
    public Func<IServiceProvider, CancellationToken, ValueTask<bool>>? DeliveryGate { get; set; }

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

    /// <summary>How often the delivery worker runs the retention purge. Defaults to 1 hour.</summary>
    /// <remarks>
    /// The purge is a maintenance sweep over already-delivered rows, far less urgent than delivery itself, so it
    /// runs on its own cadence instead of once per idle <see cref="PollingInterval"/> tick (which would issue the
    /// purge <c>DELETE</c> roughly every second on every node). Only relevant when <see cref="RetentionPeriod"/>
    /// is set.
    /// </remarks>
    public TimeSpan PurgeInterval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// Overrides the serializer used for event payloads. Defaults to <c>null</c> — the canonical Elarion JSON
    /// options (<c>IElarionJsonSerialization</c>) are used, so event DTOs resolve through the app's source-generated
    /// contexts (trim/AOT-safe). Set a non-null value only to serialize the outbox differently from the rest of the app.
    /// </summary>
    public JsonSerializerOptions? SerializerOptions { get; set; }
}
