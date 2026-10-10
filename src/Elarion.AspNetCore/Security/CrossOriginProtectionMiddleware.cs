using System.Buffers;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Elarion.AspNetCore;

/// <summary>
/// Refuses state-changing requests that a browser sent from a page on another origin, using the Fetch Metadata
/// <c>Sec-Fetch-Site</c> header and falling back to <c>Origin</c> for browsers that do not send it. A request with
/// neither header did not come from a browser page (a server, a script, server-side rendering) and passes: a
/// cross-site attack needs the victim's browser to attach its credentials, and every browser that does so sends at
/// least one of the two on an unsafe method.
/// </summary>
internal sealed class CrossOriginProtectionMiddleware(
    RequestDelegate next,
    HashSet<string> allowedOrigins,
    PathString[] exemptPathPrefixes,
    ILogger logger) {
    private const string SecFetchSiteHeader = "Sec-Fetch-Site";

    private const string RefusalDetail =
        "Cross-origin request refused: a state-changing request must come from this application's own pages.";

    private static readonly byte[] RefusalBody = CreateRefusalBody();

    public Task InvokeAsync(HttpContext context) {
        var request = context.Request;
        if (IsSafeMethod(request.Method) || IsExempt(request.Path) || IsAllowed(request)) return next(context);

        logger.LogInformation(
            "Cross-origin {Method} {Path} refused (Origin {Origin}, Sec-Fetch-Site {SecFetchSite})",
            request.Method,
            request.Path,
            request.Headers.Origin.ToString(),
            request.Headers[SecFetchSiteHeader].ToString());
        return WriteRefusalAsync(context);
    }

    // The refusal must not depend on the host's HTTP JSON configuration: this runs before routing, in hosts that
    // may map only /rpc and never call AddElarionHttpJson/AddProblemDetails (with reflection off, Results.Problem
    // would then fail with a 500). A registered IProblemDetailsService still gets to write it, so host
    // customization (trace ids, a custom writer) applies; otherwise a fixed body is written.
    private static async Task WriteRefusalAsync(HttpContext context) {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;

        var problemDetails = context.RequestServices.GetService<IProblemDetailsService>();
        if (problemDetails is not null) {
            var problem = new ProblemDetails {
                Status = StatusCodes.Status403Forbidden,
                Title = "Forbidden",
                Detail = RefusalDetail,
                Extensions = { [HttpAppErrorMapper.CodeExtensionName] = CrossOriginProtectionErrorCodes.Refused }
            };
            if (await problemDetails.TryWriteAsync(new ProblemDetailsContext {
                    HttpContext = context, ProblemDetails = problem
                }))
                return;
        }

        context.Response.ContentType = "application/problem+json";
        await context.Response.Body.WriteAsync(RefusalBody, context.RequestAborted);
    }

    private static byte[] CreateRefusalBody() {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer)) {
            writer.WriteStartObject();
            writer.WriteString("type", "https://tools.ietf.org/html/rfc9110#section-15.5.4");
            writer.WriteString("title", "Forbidden");
            writer.WriteNumber("status", StatusCodes.Status403Forbidden);
            writer.WriteString("detail", RefusalDetail);
            writer.WriteString(HttpAppErrorMapper.CodeExtensionName, CrossOriginProtectionErrorCodes.Refused);
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    // RFC 9110 safe methods. They must not change state, so a cross-site GET is harmless by contract.
    private static bool IsSafeMethod(string method) {
        return HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method) ||
               HttpMethods.IsTrace(method);
    }

    private bool IsExempt(PathString path) {
        foreach (var prefix in exemptPathPrefixes) {
            if (path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    private bool IsAllowed(HttpRequest request) {
        var origins = request.Headers.Origin;
        // Two Origin headers are never sent by a browser; refuse rather than pick one.
        if (origins.Count > 1) return false;

        var origin = origins.Count == 1 ? origins[0] : null;
        var fetchSite = request.Headers[SecFetchSiteHeader];
        if (fetchSite.Count > 0) {
            // The browser says where the request came from. "none" is a user-initiated navigation (typed URL,
            // bookmark); "same-site" is a sibling subdomain, which shares cookies but not trust, so it is refused
            // unless that origin is explicitly allowed — like "cross-site" and any value this code does not know.
            var site = fetchSite.Count == 1 ? fetchSite[0] : null;
            if (site is "same-origin" or "none") return true;

            return origin is not null && allowedOrigins.Contains(Normalize(origin) ?? string.Empty);
        }

        // An older browser without Fetch Metadata still sends Origin on an unsafe method; no Origin at all means the
        // caller is not a browser page.
        if (origin is null) return true;

        var normalized = Normalize(origin);
        if (normalized is null) return false; // "null" (sandboxed or privacy-redirected) and malformed origins

        return allowedOrigins.Contains(normalized) ||
               string.Equals(normalized, Normalize($"{request.Scheme}://{request.Host.Value}"),
                   StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Returns <c>scheme://host[:port]</c> (lower-case, default port omitted) for an absolute http(s) origin, or
    /// <see langword="null"/> when <paramref name="origin"/> is not one.
    /// </summary>
    internal static string? Normalize(string origin) {
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return null;
        if (uri.UserInfo.Length > 0 || uri.AbsolutePath != "/" || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            return null;

        return uri.GetLeftPart(UriPartial.Authority).ToLowerInvariant();
    }
}
