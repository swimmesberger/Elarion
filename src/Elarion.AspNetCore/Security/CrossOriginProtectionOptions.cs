using Microsoft.AspNetCore.Http;

namespace Elarion.AspNetCore;

/// <summary>
/// Configures the cross-origin request protection installed by
/// <see cref="CrossOriginProtectionApplicationBuilderExtensions.UseElarionCrossOriginProtection"/>.
/// </summary>
public sealed class CrossOriginProtectionOptions {
    /// <summary>
    /// Origins (<c>scheme://host[:port]</c>, for example <c>https://app.example.com</c>) whose browser pages may
    /// send state-changing requests to this host in addition to the request's own origin. List the public origin
    /// here when a reverse proxy rewrites the <c>Host</c> header or terminates TLS and the host does not apply
    /// forwarded headers, and every separate front-end origin that calls this API. Entries are validated and
    /// normalized when the middleware is installed; a path, query, or fragment is rejected.
    /// </summary>
    public IList<string> AllowedOrigins { get; } = new List<string>();

    /// <summary>
    /// Path prefixes the protection leaves alone, matched by whole segments (<c>/signin-oidc</c> matches
    /// <c>/signin-oidc</c> and <c>/signin-oidc/x</c>, not <c>/signin-oidcx</c>). Exempt a route only when a
    /// legitimate cross-site browser request must reach it (an OpenID Connect <c>form_post</c> callback, a
    /// payment provider's return post) or when it authenticates every request with its own non-ambient credential.
    /// </summary>
    public IList<PathString> ExemptPathPrefixes { get; } = new List<PathString>();
}

/// <summary>The stable error codes of the cross-origin request protection (ADR-0080).</summary>
public static class CrossOriginProtectionErrorCodes {
    /// <summary>
    /// A state-changing request came from a browser page on another origin and was refused with HTTP 403.
    /// </summary>
    public const string Refused = "cross_origin.refused";
}
