using System.Buffers.Text;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Elarion.WebPush;

/// <summary>What happened to one delivery attempt, which decides whether its subscription is kept.</summary>
internal enum WebPushDeliveryStatus {
    /// <summary>The push service accepted the message.</summary>
    Delivered,

    /// <summary>The subscription can never be delivered to (gone, malformed, or disallowed); delete it.</summary>
    Dead,

    /// <summary>A transient or unexplained failure; keep the subscription.</summary>
    Failed
}

/// <summary>
/// One RFC 8030 delivery: encrypts the payload for the subscription (RFC 8291), signs a VAPID token for the
/// push service origin (RFC 8292), POSTs, and classifies the response.
/// </summary>
internal sealed class WebPushClient(
    IHttpClientFactory httpClientFactory,
    IVapidKeyProvider keyProvider,
    VapidTokenFactory tokenFactory,
    WebPushOptions options,
    ILogger<WebPushClient> logger) {
    private const int TopicMaxLength = 32;

    public async ValueTask<WebPushDeliveryStatus> SendAsync(
        PushSubscription subscription, ReadOnlyMemory<byte> payload, WebPushMessage message,
        CancellationToken cancellationToken) {
        if (!Uri.TryCreate(subscription.Endpoint, UriKind.Absolute, out var endpoint)
            || endpoint.Scheme != Uri.UriSchemeHttps
            || !options.IsEndpointHostAllowed(endpoint.IdnHost)) {
            logger.LogWarning(
                "Removing a Web Push subscription of user {UserId}: its endpoint is not an allowed https push service.",
                subscription.UserId);
            return WebPushDeliveryStatus.Dead;
        }

        byte[] body;
        try {
            body = WebPushEncryption.Encrypt(
                payload.Span, Base64Url.DecodeFromChars(subscription.P256dh), Base64Url.DecodeFromChars(subscription.Auth));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException) {
            logger.LogWarning(
                "Removing a Web Push subscription of user {UserId} on {Host}: its keys are malformed.",
                subscription.UserId, endpoint.Host);
            return WebPushDeliveryStatus.Dead;
        }

        var keys = await keyProvider.GetAsync(cancellationToken).ConfigureAwait(false);
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.TryAddWithoutValidation("Authorization",
            tokenFactory.GetAuthorizationHeader(endpoint.GetLeftPart(UriPartial.Authority), options.Subject!, keys));
        var timeToLive = message.TimeToLive ?? options.DefaultTimeToLive;
        request.Headers.TryAddWithoutValidation("TTL",
            ((long)Math.Max(0, timeToLive.TotalSeconds)).ToString(CultureInfo.InvariantCulture));
        request.Headers.TryAddWithoutValidation("Urgency", FormatUrgency(message.Urgency));
        if (message.Tag is { Length: > 0 } tag) request.Headers.TryAddWithoutValidation("Topic", ToTopic(tag));
        request.Content = new ByteArrayContent(body);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        request.Content.Headers.ContentEncoding.Add("aes128gcm");

        try {
            var client = httpClientFactory.CreateClient(WebPushOptions.HttpClientName);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            if (response.IsSuccessStatusCode) return WebPushDeliveryStatus.Delivered;
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone) {
                logger.LogDebug("Removing an expired Web Push subscription of user {UserId} on {Host} ({StatusCode}).",
                    subscription.UserId, endpoint.Host, (int)response.StatusCode);
                return WebPushDeliveryStatus.Dead;
            }

            logger.LogWarning("Web Push delivery to user {UserId} on {Host} failed with {StatusCode}.",
                subscription.UserId, endpoint.Host, (int)response.StatusCode);
            return WebPushDeliveryStatus.Failed;
        }
        catch (Exception ex) when (ex is HttpRequestException
                                       || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested)) {
            // HttpClient reports its own timeout as a cancellation the caller did not request.
            logger.LogWarning(ex, "Web Push delivery to user {UserId} on {Host} failed.", subscription.UserId,
                endpoint.Host);
            return WebPushDeliveryStatus.Failed;
        }
    }

    internal static string FormatUrgency(WebPushUrgency urgency) {
        return urgency switch {
            WebPushUrgency.VeryLow => "very-low",
            WebPushUrgency.Low => "low",
            WebPushUrgency.High => "high",
            _ => "normal"
        };
    }

    /// <summary>
    /// RFC 8030 §5.4 limits a topic to 32 URL-safe base64 characters. A conforming tag passes through
    /// unchanged; any other tag is hashed, which keeps equal tags equal (the property replacement needs).
    /// </summary>
    internal static string ToTopic(string tag) {
        if (tag.Length <= TopicMaxLength && tag.All(IsUrlSafeBase64Char)) return tag;
        return Base64Url.EncodeToString(SHA256.HashData(Encoding.UTF8.GetBytes(tag)))[..TopicMaxLength];
    }

    private static bool IsUrlSafeBase64Char(char c) {
        return char.IsAsciiLetterOrDigit(c) || c is '-' or '_';
    }
}
