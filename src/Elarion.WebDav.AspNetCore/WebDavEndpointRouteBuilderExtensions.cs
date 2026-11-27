using System.Security.Claims;
using Elarion.Abstractions.Dispatch;
using Elarion.AspNetCore;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Elarion.WebDav.AspNetCore;

/// <summary>The options of <see cref="WebDavEndpointRouteBuilderExtensions.MapElarionDav"/>.</summary>
public sealed class WebDavHostOptions {
    /// <summary>
    /// The name of the app-password scheme of <c>Elarion.AppPasswords.AspNetCore</c>, <c>ElarionAppPassword</c>: the DAV
    /// routes' default scheme.
    /// </summary>
    public const string AppPasswordScheme = "ElarionAppPassword";

    /// <summary>
    /// The authentication schemes the DAV route group accepts, and only these (spec 0006 FR-033): an app password then
    /// authenticates no other endpoint, and the application's browser credential no DAV request. Default the app-password
    /// scheme.
    /// </summary>
    public IList<string> AuthenticationSchemes { get; } = new List<string> { AppPasswordScheme };

    /// <summary>Whether <c>/.well-known/{protocol}</c> (RFC 6764) is mapped at the application root. Default true.</summary>
    public bool MapWellKnown { get; set; } = true;
}

/// <summary>Maps the DAV routes (ADR-0111).</summary>
public static class WebDavEndpointRouteBuilderExtensions {
    /// <summary>
    /// The methods the DAV routes take. The write methods are answered <c>403</c> by the dispatcher; the
    /// <c>Allow</c> header lists none of them.
    /// </summary>
    public static IReadOnlyList<string> Methods { get; } = [
        "OPTIONS", "GET", "HEAD", "PROPFIND", "REPORT", "PUT", "DELETE", "PROPPATCH", "MKCOL", "MKCALENDAR", "MOVE", "COPY", "POST", "LOCK",
        "UNLOCK", "ACL",
    ];

    /// <summary>
    /// Maps one route group under <c>WebDavOptions.Prefix</c> (default <c>/dav</c>) for every registered DAV protocol and
    /// <c>/.well-known/{protocol}</c> at the root, redirecting with <c>301</c> to the prefix (spec 0006 FR-014–FR-016,
    /// FR-033, FR-036). The group requires an authenticated user of <see cref="WebDavHostOptions.AuthenticationSchemes"/>,
    /// seeds the request's dispatch scope with that user after authentication, so <c>ICurrentUser</c> and the calendar
    /// sources see the DAV principal, and is exempt from <c>UseElarionCrossOriginProtection</c>.
    /// </summary>
    /// <param name="endpoints">The endpoint route builder (the application).</param>
    /// <param name="configure">Adjusts the host options.</param>
    /// <returns>The DAV route group, for further conventions such as a rate limiter.</returns>
    /// <remarks>
    /// Requires <c>AddElarionWebDav</c> (or <c>AddElarionCalDav</c>), an authentication scheme (normally
    /// <c>AddElarionAppPasswordAuthentication()</c>, whose feed-token reader must match the prefix when it is not
    /// <c>/dav</c>), <c>UseAuthentication</c> and <c>UseAuthorization</c>.
    /// </remarks>
    /// <example>
    /// <code>
    /// builder.Services.AddElarionCalDav(builder.Configuration.GetSection("CalDav"));
    /// builder.Services.AddAuthentication().AddElarionAppPasswordAuthentication(o => o.Events.OnCreatePrincipal = ...);
    /// var app = builder.Build();
    /// app.UseAuthentication();
    /// app.UseAuthorization();
    /// app.MapElarionDav();
    /// </code>
    /// </example>
    public static RouteGroupBuilder MapElarionDav(this IEndpointRouteBuilder endpoints, Action<WebDavHostOptions>? configure = null) {
        ArgumentNullException.ThrowIfNull(endpoints);
        var hostOptions = new WebDavHostOptions();
        configure?.Invoke(hostOptions);
        if (hostOptions.AuthenticationSchemes.Count == 0)
            throw new ArgumentException("The DAV routes need at least one authentication scheme.", nameof(configure));

        var services = endpoints.ServiceProvider;
        var dispatcher = services.GetService<DavDispatcher>() ?? throw new InvalidOperationException(
            "MapElarionDav needs the WebDAV services; call AddElarionCalDav() or AddElarionWebDav() first.");
        var prefix = new PathString(services.GetRequiredService<IOptions<WebDavOptions>>().Value.Prefix);

        var policy = new AuthorizationPolicyBuilder([.. hostOptions.AuthenticationSchemes]).RequireAuthenticatedUser().Build();
        var group = endpoints.MapGroup(prefix);
        group.MapMethods("/{**path}", Methods, context => HandleAsync(context, prefix));
        group.RequireAuthorization(policy).DisableElarionCrossOriginProtection(prefix);

        if (hostOptions.MapWellKnown)
            foreach (var name in dispatcher.Protocols.Select(static p => p.WellKnownName).Distinct(StringComparer.Ordinal))
                endpoints.MapMethods("/.well-known/" + name, Methods, context => RedirectAsync(context, prefix)).AllowAnonymous();

        return group;
    }

    // RFC 6764 §5: the well-known URI redirects to the context path and serves nothing itself. iOS, Thunderbird and DAVx5
    // send PROPFIND here, so every method is redirected; the Location is relative, so it keeps the scheme the client used.
    /// <summary>
    /// The request path below the prefix, still percent-encoded as the client sent it. The dispatcher decodes it once;
    /// <see cref="HttpRequest.Path"/> is already decoded, and re-encoding it would keep a literal "%20" in a name as "%20",
    /// which the dispatcher would decode a second time (Apple caldavtester M-09: "%2520" names a member called "%20").
    /// </summary>
    internal static string EscapedPathBelow(HttpContext context, PathString prefix) {
        var request = context.Request;
        var raw = context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpRequestFeature>()?.RawTarget;
        if (string.IsNullOrEmpty(raw) || raw[0] != '/') {
            // No origin-form target (a test server, an absolute-form request): re-encode the decoded path.
            var path = request.Path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase, out var rest) ? rest : request.Path;
            return path.ToUriComponent();
        }

        var end = raw.IndexOfAny(['?', '#']);
        if (end >= 0) raw = raw[..end];
        // Drop as many segments as the path base and the prefix hold; routing matched them on the decoded path already.
        var skip = (request.PathBase + prefix).Value?.Split('/', StringSplitOptions.RemoveEmptyEntries).Length ?? 0;
        var below = string.Join('/', raw.Split('/', StringSplitOptions.RemoveEmptyEntries).Skip(skip));
        return "/" + below + (raw.EndsWith('/') && below.Length > 0 ? "/" : "");
    }

    private static Task RedirectAsync(HttpContext context, PathString prefix) {
        context.Response.StatusCode = StatusCodes.Status301MovedPermanently;
        context.Response.Headers.Location = (context.Request.PathBase + prefix).Add("/").ToUriComponent();
        context.Response.Headers.CacheControl = "no-cache";
        return Task.CompletedTask;
    }

    private static async Task HandleAsync(HttpContext context, PathString prefix) {
        var services = context.RequestServices;
        // Authentication of the DAV scheme ran in the authorization middleware, after any middleware that seeded the scope
        // with the browser principal; seed it again with the DAV principal, so ICurrentUser and the sources see it.
        var dispatchContext = new DispatchScopeContext();
        dispatchContext.Set<ClaimsPrincipal>(context.User);
        services.SeedScope(dispatchContext);

        var request = context.Request;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in (ReadOnlySpan<string>)["Depth", "If-Match", "If-None-Match", "Prefer", "Brief", "CalDAV-Timezones"])
            if (request.Headers.TryGetValue(name, out var value))
                headers[name] = value.ToString();

        var response = await services.GetRequiredService<DavDispatcher>().HandleAsync(new DavRequest {
            Method = request.Method,
            Path = EscapedPathBelow(context, prefix),
            HrefBase = (request.PathBase + prefix).ToUriComponent(),
            Headers = headers,
            Body = request.Body,
            ContentLength = request.ContentLength,
        }, services, context.RequestAborted).ConfigureAwait(false);

        context.Response.StatusCode = response.StatusCode;
        foreach (var (name, value) in response.Headers) context.Response.Headers[name] = value;
        if (response.ContentType is not null) context.Response.ContentType = response.ContentType;
        if (response.ContentLength is { } length) context.Response.ContentLength = length;
        if (HttpMethods.IsHead(request.Method)) return;
        await response.WriteBodyAsync(context.Response.Body, context.RequestAborted).ConfigureAwait(false);
    }
}
