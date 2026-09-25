using System.Buffers;
using System.Text.Encodings.Web;
using System.Text.Json;
using Elarion.Abstractions.Identity;

namespace Elarion.WebPush;

/// <summary>
/// Delivers a <see cref="WebPushMessage"/> to every browser a user subscribed, whether or not the app is open.
/// Subscriptions the push service reports gone (404/410), or whose keys are malformed, are deleted as part of
/// the send; transient failures leave them in place. Delivery is best-effort and at-most-once per send — a
/// failed send is not retried, so a caller that must not lose a notification sends it again from its own
/// durable record (an integration event, a job).
/// </summary>
public interface IWebPushSender {
    /// <summary>Sends to every subscription owned by any of <paramref name="userIds"/>.</summary>
    /// <param name="userIds">The recipients.</param>
    /// <param name="message">The notification.</param>
    /// <param name="cancellationToken">Cancels the fan-out.</param>
    /// <returns>How many subscriptions were attempted, delivered, removed, and failed.</returns>
    /// <exception cref="ArgumentException">The serialized message is larger than one push message can carry.</exception>
    ValueTask<WebPushResult> SendToUsersAsync(IReadOnlyCollection<string> userIds, WebPushMessage message,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends to the current user's subscriptions (<see cref="ICurrentUser"/>) — a "send a test notification"
    /// button. Sends nothing (<see cref="WebPushResult.Empty"/>) when no user is authenticated.
    /// </summary>
    /// <param name="message">The notification.</param>
    /// <param name="cancellationToken">Cancels the fan-out.</param>
    ValueTask<WebPushResult> SendToCurrentUserAsync(WebPushMessage message,
        CancellationToken cancellationToken = default);
}

/// <summary>The default <see cref="IWebPushSender"/>: a bounded-concurrency fan-out over the subscription store.</summary>
internal sealed class WebPushSender(
    IPushSubscriptionStore store,
    WebPushClient client,
    WebPushOptions options,
    ICurrentUser? currentUser = null) : IWebPushSender {
    public async ValueTask<WebPushResult> SendToUsersAsync(IReadOnlyCollection<string> userIds,
        WebPushMessage message, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(userIds);
        ArgumentNullException.ThrowIfNull(message);
        // Serialize (and size-check) before touching the store, so an oversized message fails for every caller.
        var payload = SerializePayload(message);
        if (userIds.Count == 0) return WebPushResult.Empty;

        var subscriptions = await store.ListByUsersAsync(userIds, cancellationToken).ConfigureAwait(false);
        if (subscriptions.Count == 0) return WebPushResult.Empty;

        int delivered = 0, removed = 0, failed = 0;
        await Parallel.ForEachAsync(
                subscriptions,
                new ParallelOptions {
                    MaxDegreeOfParallelism = options.MaxConcurrentSends, CancellationToken = cancellationToken
                },
                async (subscription, ct) => {
                    switch (await client.SendAsync(subscription, payload, message, ct).ConfigureAwait(false)) {
                        case WebPushDeliveryStatus.Delivered:
                            Interlocked.Increment(ref delivered);
                            break;
                        case WebPushDeliveryStatus.Dead:
                            // By endpoint regardless of owner: a dead endpoint is dead for whoever holds it now.
                            if (await store.RemoveAsync(subscription.Endpoint, cancellationToken: ct).ConfigureAwait(false))
                                Interlocked.Increment(ref removed);
                            break;
                        default:
                            Interlocked.Increment(ref failed);
                            break;
                    }
                })
            .ConfigureAwait(false);

        return new WebPushResult {
            Attempted = subscriptions.Count, Delivered = delivered, Removed = removed, Failed = failed
        };
    }

    public ValueTask<WebPushResult> SendToCurrentUserAsync(WebPushMessage message,
        CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(message);
        if (currentUser is not { IsAuthenticated: true, UserId: { Length: > 0 } userId })
            return ValueTask.FromResult(WebPushResult.Empty);
        return SendToUsersAsync([userId], message, cancellationToken);
    }

    /// <summary>
    /// The <c>{title, body, url, tag}</c> wire payload the service-worker module reads. Written directly
    /// rather than through the canonical serializer: it is a fixed browser-facing contract, not an
    /// application DTO, and must not change shape with the host's JSON configuration.
    /// </summary>
    internal static byte[] SerializePayload(WebPushMessage message) {
        var buffer = new ArrayBufferWriter<byte>(256);
        // Relaxed escaping: the payload is encrypted JSON read by JSON.parse, never embedded in HTML, and
        // escaping every non-ASCII character would spend the 3993-byte budget on \uXXXX sequences.
        using (var writer = new Utf8JsonWriter(buffer,
                   new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping })) {
            writer.WriteStartObject();
            writer.WriteString("title", message.Title);
            writer.WriteString("body", message.Body);
            if (message.Url is not null) writer.WriteString("url", message.Url);
            if (message.Tag is not null) writer.WriteString("tag", message.Tag);
            writer.WriteEndObject();
        }

        if (buffer.WrittenCount > WebPushEncryption.MaxPlaintextLength)
            throw new ArgumentException(
                $"The Web Push message serializes to {buffer.WrittenCount} bytes; at most "
                + $"{WebPushEncryption.MaxPlaintextLength} fit one push message. Shorten the text and let the "
                + "notification's URL lead to the details.",
                nameof(message));
        return buffer.WrittenSpan.ToArray();
    }
}
