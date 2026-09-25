namespace Elarion.WebPush;

/// <summary>Web Push configuration, set through <c>AddElarionWebPush(options => …)</c>.</summary>
public sealed class WebPushOptions {
    /// <summary>The named <see cref="HttpClient"/> deliveries use; configure it to add resilience or a proxy.</summary>
    public const string HttpClientName = "Elarion.WebPush";

    /// <summary>
    /// The push services the default <see cref="AllowedEndpointHosts"/> admits (a host matches itself and
    /// its subdomains): Google FCM (Chrome, Edge, Android), Mozilla autopush (Firefox), Apple (Safari,
    /// iOS/iPadOS Home Screen apps), and Windows Notification Service (legacy Edge).
    /// </summary>
    public static readonly IReadOnlyList<string> WellKnownPushServiceHosts = [
        "fcm.googleapis.com",
        "android.googleapis.com",
        "push.services.mozilla.com",
        "push.apple.com",
        "notify.windows.com"
    ];

    /// <summary>
    /// Required. The VAPID contact (RFC 8292 <c>sub</c>): a <c>mailto:</c> or <c>https:</c> URI the push
    /// service operator can reach you at when your traffic misbehaves.
    /// </summary>
    public string? Subject { get; set; }

    /// <summary>
    /// A configured VAPID public key (base64url, uncompressed P-256 point). Set together with
    /// <see cref="PrivateKey"/> to pin the key pair from configuration or a secret store; when both are
    /// unset the key pair is read from the <see cref="IVapidKeyStore"/> and generated on first use.
    /// </summary>
    public string? PublicKey { get; set; }

    /// <summary>The configured VAPID private key (base64url, 32-byte P-256 scalar). See <see cref="PublicKey"/>.</summary>
    public string? PrivateKey { get; set; }

    /// <summary>
    /// How long a push service keeps a message for an offline device when <see cref="WebPushMessage.TimeToLive"/>
    /// is unset. Defaults to one day: a notification that arrives days late is usually noise.
    /// </summary>
    public TimeSpan DefaultTimeToLive { get; set; } = TimeSpan.FromDays(1);

    /// <summary>How many push-service requests one fan-out keeps in flight. Defaults to 8.</summary>
    public int MaxConcurrentSends { get; set; } = 8;

    /// <summary>
    /// The endpoint hosts a subscription may point at. The server POSTs to the endpoint a browser hands
    /// it, so an unrestricted endpoint lets any authenticated user aim server-side requests at an
    /// internal address; the default admits only <see cref="WellKnownPushServiceHosts"/>. A host matches
    /// itself and its subdomains. Add a host for another push service, or set
    /// <see cref="AllowAnyEndpointHost"/> for a self-hosted one.
    /// </summary>
    public ISet<string> AllowedEndpointHosts { get; } =
        new HashSet<string>(WellKnownPushServiceHosts, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Admits any <c>https</c> endpoint host, ignoring <see cref="AllowedEndpointHosts"/>. Off by default;
    /// turn it on only when every caller that can register a subscription is trusted.
    /// </summary>
    public bool AllowAnyEndpointHost { get; set; }

    internal void Validate() {
        if (string.IsNullOrWhiteSpace(Subject))
            throw new InvalidOperationException(
                "WebPushOptions.Subject is required: set a mailto: or https: contact the push service can reach "
                + "(AddElarionWebPush(options => options.Subject = \"mailto:ops@example.com\")).");
        if (!Subject.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)
            && !Subject.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"WebPushOptions.Subject '{Subject}' must be a mailto: or https: URI (RFC 8292).");

        if (string.IsNullOrEmpty(PublicKey) != string.IsNullOrEmpty(PrivateKey))
            throw new InvalidOperationException(
                "WebPushOptions.PublicKey and PrivateKey must be configured together (or both left unset).");
        if (!string.IsNullOrEmpty(PublicKey))
            new VapidKeys { PublicKey = PublicKey, PrivateKey = PrivateKey! }.Validate();

        if (DefaultTimeToLive < TimeSpan.Zero)
            throw new InvalidOperationException("WebPushOptions.DefaultTimeToLive must not be negative.");
        if (MaxConcurrentSends < 1)
            throw new InvalidOperationException("WebPushOptions.MaxConcurrentSends must be at least 1.");
    }

    internal bool IsEndpointHostAllowed(string host) {
        if (AllowAnyEndpointHost) return true;
        foreach (var allowed in AllowedEndpointHosts) {
            if (host.Equals(allowed, StringComparison.OrdinalIgnoreCase)) return true;
            if (host.Length > allowed.Length
                && host.EndsWith(allowed, StringComparison.OrdinalIgnoreCase)
                && host[host.Length - allowed.Length - 1] == '.')
                return true;
        }

        return false;
    }
}
