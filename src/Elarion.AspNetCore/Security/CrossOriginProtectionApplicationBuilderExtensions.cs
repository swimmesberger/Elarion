using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Elarion.AspNetCore;

/// <summary>Installs the cross-origin request protection (CSRF defense for cookie-authenticated hosts).</summary>
public static class CrossOriginProtectionApplicationBuilderExtensions {
    /// <summary>
    /// Refuses state-changing requests (every method except GET, HEAD, OPTIONS, and TRACE) and WebSocket handshakes
    /// that a browser sent from a page on another origin, with HTTP 403 and a ProblemDetails body whose <c>code</c> is
    /// <see cref="CrossOriginProtectionErrorCodes.Refused"/>. Use it when browsers authenticate with a credential
    /// they attach on their own — a cookie (ASP.NET Core Identity, an authenticating reverse proxy's session
    /// cookie), cached HTTP Basic/Negotiate credentials, or a client certificate. It covers <c>/rpc</c>, form and
    /// file <c>[HttpEndpoint]</c> routes, blob uploads, WebSocket endpoints such as
    /// <c>MapElarionConnectionSocket</c>, and hand-written routes alike, needs no token in the client, and needs no
    /// identity, so place it early: before authentication, before <c>UseWebSockets</c>, and before routing.
    /// </summary>
    /// <remarks>
    /// <para>The decision, in order:</para>
    /// <list type="number">
    ///   <item>
    ///     A safe method that is not a WebSocket handshake (a GET whose <c>Upgrade</c> header names
    ///     <c>websocket</c>), or a path under <see cref="CrossOriginProtectionOptions.ExemptPathPrefixes"/>, passes.
    ///   </item>
    ///   <item>More than one <c>Origin</c> header is refused.</item>
    ///   <item>
    ///     With <c>Sec-Fetch-Site</c> (every current browser): <c>same-origin</c> and <c>none</c> pass; any other
    ///     value (<c>same-site</c>, <c>cross-site</c>) passes only when <c>Origin</c> is one of
    ///     <see cref="CrossOriginProtectionOptions.AllowedOrigins"/>.
    ///   </item>
    ///   <item>
    ///     Without it: no <c>Origin</c> passes (not a browser page); an <c>Origin</c> passes when it is the
    ///     request's own origin (scheme and <c>Host</c> as this host sees them) or one of the allowed origins.
    ///   </item>
    /// </list>
    /// <para>
    /// Behind a reverse proxy, apply forwarded headers first (<c>UseForwardedHeaders</c>) or list the public origin in
    /// <see cref="CrossOriginProtectionOptions.AllowedOrigins"/>; otherwise an older browser's same-origin request is
    /// compared against the internal scheme and host and refused (fail closed). The protection does not replace
    /// CORS: a cross-origin front end still needs a CORS policy for the browser to send JSON to this host.
    /// </para>
    /// </remarks>
    /// <param name="app">The application builder.</param>
    /// <param name="configure">Configures allowed origins and exempt path prefixes.</param>
    /// <returns>The application builder for chaining.</returns>
    /// <exception cref="ArgumentException">An allowed origin is not an absolute <c>http</c>/<c>https</c> origin.</exception>
    /// <example>
    /// <code>
    /// app.UseForwardedHeaders();
    /// app.UseElarionCrossOriginProtection(o => {
    ///     o.AllowedOrigins.Add("https://app.example.com");
    ///     o.ExemptPathPrefixes.Add("/signin-oidc");   // the identity provider's form_post callback
    /// });
    /// app.UseAuthentication();
    /// app.UseAuthorization();
    /// </code>
    /// </example>
    public static IApplicationBuilder UseElarionCrossOriginProtection(
        this IApplicationBuilder app,
        Action<CrossOriginProtectionOptions>? configure = null) {
        ArgumentNullException.ThrowIfNull(app);

        var options = new CrossOriginProtectionOptions();
        configure?.Invoke(options);

        var allowedOrigins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var origin in options.AllowedOrigins) {
            var normalized = CrossOriginProtectionMiddleware.Normalize(origin) ?? throw new ArgumentException(
                $"Allowed origin '{origin}' is not an absolute http(s) origin of the form scheme://host[:port] " +
                "(no path, query, or fragment).",
                nameof(configure));
            allowedOrigins.Add(normalized);
        }

        var exemptPathPrefixes = options.ExemptPathPrefixes.ToArray();
        foreach (var prefix in exemptPathPrefixes) {
            if (!prefix.HasValue || prefix.Value == "/")
                throw new ArgumentException(
                    "An exempt path prefix must name a path below the root (e.g. \"/signin-oidc\"); exempting \"/\" " +
                    "would turn the protection off.",
                    nameof(configure));
        }

        var logger = app.ApplicationServices.GetRequiredService<ILoggerFactory>()
            .CreateLogger("Elarion.AspNetCore.CrossOriginProtection");
        return app.Use(next =>
            new CrossOriginProtectionMiddleware(next, allowedOrigins, exemptPathPrefixes, logger).InvokeAsync);
    }
}
