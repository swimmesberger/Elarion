using System.Buffers.Text;
using Elarion.Abstractions;
using Elarion.Abstractions.Identity;

namespace Elarion.WebPush;

/// <summary>
/// A browser subscription as the Push API serializes it — <c>JSON.stringify(subscription)</c> /
/// <c>subscription.toJSON()</c> — so the client posts it unchanged.
/// </summary>
public sealed record PushSubscriptionRequest {
    /// <summary>The push service endpoint URL.</summary>
    public required string Endpoint { get; init; }

    /// <summary>The subscription's encryption keys.</summary>
    public required PushSubscriptionKeys Keys { get; init; }

    /// <summary>When the browser will expire the subscription (Unix milliseconds), if it says. Informational.</summary>
    public long? ExpirationTime { get; init; }
}

/// <summary>The <c>keys</c> member of a serialized browser subscription.</summary>
public sealed record PushSubscriptionKeys {
    /// <summary>The P-256 public key, base64url.</summary>
    public required string P256dh { get; init; }

    /// <summary>The 16-byte authentication secret, base64url.</summary>
    public required string Auth { get; init; }
}

/// <summary>
/// The server half of subscribe/unsubscribe for the current user, shared by <c>MapElarionWebPush</c> and
/// application <c>[Handler]</c>s: it validates what the browser sent, binds the subscription to
/// <see cref="ICurrentUser"/>, and returns a normal <see cref="Result"/>. Authorization of the calling
/// endpoint or handler stays the host's (its usual policy or <c>[Require*]</c> attributes); this service only
/// insists the caller is authenticated, so a subscription always has an owner.
/// </summary>
/// <example>
/// <code>
/// [Handler("webPush.subscribe")]
/// public sealed class SubscribeToPush(WebPushSubscriptionService subscriptions)
///     : IHandler&lt;PushSubscriptionRequest&gt; {
///     public ValueTask&lt;Result&gt; HandleAsync(PushSubscriptionRequest request, CancellationToken ct) =>
///         subscriptions.SubscribeAsync(request, cancellationToken: ct);
/// }
/// </code>
/// </example>
public sealed class WebPushSubscriptionService(
    IPushSubscriptionStore store,
    IVapidKeyProvider keyProvider,
    WebPushOptions options,
    TimeProvider timeProvider,
    ICurrentUser? currentUser = null) {
    /// <summary>The longest endpoint accepted; real push-service endpoints are a few hundred characters.</summary>
    public const int MaxEndpointLength = 2048;

    /// <summary>The longest stored user agent; longer values are truncated.</summary>
    public const int MaxUserAgentLength = 512;

    /// <summary>
    /// The VAPID public key a browser passes as <c>applicationServerKey</c> to <c>pushManager.subscribe</c>
    /// (base64url). Public by nature; generating it on first use is why this is asynchronous.
    /// </summary>
    /// <param name="cancellationToken">Cancels a first-use key resolution.</param>
    public async ValueTask<string> GetPublicKeyAsync(CancellationToken cancellationToken = default) {
        return (await keyProvider.GetAsync(cancellationToken).ConfigureAwait(false)).PublicKey;
    }

    /// <summary>
    /// Stores <paramref name="request"/> for the current user, reassigning it when the same browser was
    /// subscribed under another account. Idempotent: call it on every app start to heal a rotated
    /// subscription and refresh its last-seen time.
    /// </summary>
    /// <param name="request">The serialized browser subscription.</param>
    /// <param name="userAgent">The caller's <c>User-Agent</c>, if the transport has one.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>Success, <see cref="ErrorKind.Unauthorized"/> without a user, or <see cref="ErrorKind.Validation"/>.</returns>
    public async ValueTask<Result> SubscribeAsync(PushSubscriptionRequest request, string? userAgent = null,
        CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(request);
        if (!TryGetUserId(out var userId)) return AppError.Unauthorized("Subscribing to push notifications requires a signed-in user.");
        if (Validate(request) is { } error) return error;

        var now = timeProvider.GetUtcNow();
        await store.UpsertAsync(new PushSubscription {
            Endpoint = request.Endpoint,
            P256dh = request.Keys.P256dh,
            Auth = request.Keys.Auth,
            UserId = userId,
            UserAgent = string.IsNullOrEmpty(userAgent) ? null : userAgent[..Math.Min(userAgent.Length, MaxUserAgentLength)],
            CreatedAt = now,
            LastSeenAt = now
        }, cancellationToken).ConfigureAwait(false);
        return Result.Success();
    }

    /// <summary>
    /// Deletes the current user's subscription for <paramref name="endpoint"/>. Succeeds when there is
    /// nothing to delete, and never deletes another user's subscription.
    /// </summary>
    /// <param name="endpoint">The subscription endpoint.</param>
    /// <param name="cancellationToken">Cancels the delete.</param>
    public async ValueTask<Result> UnsubscribeAsync(string endpoint, CancellationToken cancellationToken = default) {
        if (!TryGetUserId(out var userId)) return AppError.Unauthorized("Unsubscribing from push notifications requires a signed-in user.");
        if (string.IsNullOrEmpty(endpoint) || endpoint.Length > MaxEndpointLength)
            return AppError.Validation("The subscription endpoint is missing or too long.");

        await store.RemoveAsync(endpoint, userId, cancellationToken).ConfigureAwait(false);
        return Result.Success();
    }

    private bool TryGetUserId(out string userId) {
        if (currentUser is { IsAuthenticated: true, UserId: { Length: > 0 } id }) {
            userId = id;
            return true;
        }

        userId = "";
        return false;
    }

    private AppError? Validate(PushSubscriptionRequest request) {
        if (string.IsNullOrEmpty(request.Endpoint) || request.Endpoint.Length > MaxEndpointLength
                                                   || !Uri.TryCreate(request.Endpoint, UriKind.Absolute, out var endpoint)
                                                   || endpoint.Scheme != Uri.UriSchemeHttps)
            return AppError.Validation("The subscription endpoint must be an absolute https URL.");
        // Fail closed on the SSRF surface: the server will POST to this URL on the user's behalf.
        if (!options.IsEndpointHostAllowed(endpoint.IdnHost))
            return AppError.Validation("The subscription endpoint is not a known push service.");
        if (request.Keys is null) return AppError.Validation("The subscription keys are missing.");

        if (!TryDecode(request.Keys.Auth, out var auth) || auth.Length != WebPushEncryption.AuthSecretLength)
            return AppError.Validation("The subscription auth secret must be 16 bytes of base64url.");
        if (!TryDecode(request.Keys.P256dh, out var p256dh) || !IsValidPublicKey(p256dh))
            return AppError.Validation("The subscription p256dh key must be a base64url P-256 public key.");
        return null;
    }

    private static bool TryDecode(string? value, out byte[] bytes) {
        bytes = [];
        if (string.IsNullOrEmpty(value)) return false;
        try {
            bytes = Base64Url.DecodeFromChars(value);
            return true;
        }
        catch (FormatException) {
            return false;
        }
    }

    private static bool IsValidPublicKey(byte[] encoded) {
        using var key = P256.TryImportPublicKey(encoded);
        return key is not null;
    }
}
