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
    /// <summary>How much of a refusal's body goes into the log — enough for a reason, not for an error page.</summary>
    internal const int MaxReasonLength = 256;

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
        // No RFC 8030 Topic header, even for a tagged message: Apple's push service rejects every request
        // that carries one (400 BadWebPushTopic), whatever its value. The tag travels in the payload, where
        // the browser still replaces a displayed notification with the same tag.
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

            var reason = await ReadReasonAsync(response, cancellationToken).ConfigureAwait(false);
            logger.LogWarning("Web Push delivery to user {UserId} on {Host} failed with {StatusCode}: {Reason}",
                subscription.UserId, endpoint.Host, (int)response.StatusCode, reason ?? "(no reason given)");
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

    /// <summary>
    /// Push services say why they refused a message in the body — Apple as <c>{"reason":"BadWebPushTopic"}</c>,
    /// FCM and Mozilla as a sentence — and nowhere else, so the reason belongs in the warning: a bare status
    /// code leaves "every push to iPhones fails with 400" undiagnosable. Read bounded and flattened to one
    /// line, so neither a large error page nor a line break ends up in the log.
    /// </summary>
    internal static async ValueTask<string?> ReadReasonAsync(HttpResponseMessage response, CancellationToken cancellationToken) {
        try {
            var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false)) {
                using var reader = new StreamReader(stream, Encoding.UTF8);
                var buffer = new char[MaxReasonLength];
                var read = await reader.ReadBlockAsync(buffer, cancellationToken).ConfigureAwait(false);
                var reason = new string(buffer, 0, read).ReplaceLineEndings(" ").Trim();
                return reason.Length == 0 ? null : reason;
            }
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException) {
            return null;
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
}
