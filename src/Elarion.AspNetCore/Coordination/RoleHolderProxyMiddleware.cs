using Elarion.Abstractions.Coordination;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Elarion.AspNetCore;

/// <summary>
/// Forwards matching requests to the current role-lease holder when this instance is not it
/// (ADR-0050) — the in-app version of the ingress rule you will eventually write, so a homogeneous
/// fleet works out of the box: the holder serves locally, every other instance transparently proxies.
/// Nothing executes locally on the proxy path (the decision is made before routing), so there is no
/// double-execution hazard; the original request — auth headers included — replays verbatim against
/// the same application on the holder.
/// </summary>
internal sealed class RoleHolderProxyMiddleware(
    IRoleLease lease,
    PathString[] pathPrefixes,
    HttpMessageInvoker client,
    ILogger logger) {
    /// <summary>Marks a forwarded request so a mid-failover receiver answers 503 instead of re-forwarding.</summary>
    public const string ProxiedHeaderName = "Elarion-Role-Proxied";

    // Hop-by-hop headers never cross a proxy (RFC 9110 §7.6.1); Host is set from the target.
    private static readonly string[] HopByHopHeaders = [
        "Connection", "Keep-Alive", "Proxy-Authenticate", "Proxy-Authorization",
        "Proxy-Connection", "TE", "Trailer", "Transfer-Encoding", "Upgrade", "Host"
    ];

    public async Task InvokeAsync(HttpContext context, RequestDelegate next) {
        // The holder's fast path: one lock-free lease check, then out of the way.
        if (lease.IsHeld) {
            await next(context);
            return;
        }

        var path = context.Request.Path;
        var matches = false;
        foreach (var prefix in pathPrefixes) {
            if (path.StartsWithSegments(prefix)) {
                matches = true;
                break;
            }
        }

        if (!matches) {
            await next(context);
            return;
        }

        if (context.Request.Headers.ContainsKey(ProxiedHeaderName)) {
            // Already forwarded once and this instance still isn't the holder: the lease is mid-failover.
            // Never re-forward — one hop, bounded.
            await Reject(context, $"The '{lease.Role}' role lease is moving between instances; retry shortly.");
            return;
        }

        var address = lease.CurrentHolderAddress;
        if (address is null) {
            await Reject(
                context,
                $"The '{lease.Role}' role holder is unknown or does not advertise an address. Register "
                + "AddElarionInstanceAddress() (or set RoleLeaseOptions.AdvertisedAddress) on every instance.");
            return;
        }

        await ProxyAsync(context, address);
    }

    private async Task ProxyAsync(HttpContext context, string address) {
        var request = context.Request;
        var targetUri = new Uri(
            address.TrimEnd('/') + request.PathBase + request.Path + request.QueryString,
            UriKind.Absolute);

        using var upstreamRequest = new HttpRequestMessage(new HttpMethod(request.Method), targetUri);
        if (request.ContentLength > 0 || request.Headers.ContainsKey("Transfer-Encoding")) {
            upstreamRequest.Content = new StreamContent(request.Body);
        }

        foreach (var header in request.Headers) {
            if (HopByHopHeaders.Contains(header.Key, StringComparer.OrdinalIgnoreCase)) {
                continue;
            }

            if (!upstreamRequest.Headers.TryAddWithoutValidation(header.Key, (IEnumerable<string?>)header.Value)) {
                upstreamRequest.Content?.Headers.TryAddWithoutValidation(header.Key, (IEnumerable<string?>)header.Value);
            }
        }

        upstreamRequest.Headers.TryAddWithoutValidation(ProxiedHeaderName, lease.Role);

        HttpResponseMessage upstreamResponse;
        try {
            // HttpMessageInvoker streams: the response returns after headers, bodies flow through.
            upstreamResponse = await client.SendAsync(upstreamRequest, context.RequestAborted);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) {
            return; // the caller went away; nothing to answer
        }
        catch (HttpRequestException ex) {
            logger.LogWarning(
                ex, "Proxying to the '{Role}' role holder at {Address} failed.", lease.Role, address);
            await Reject(
                context,
                $"The '{lease.Role}' role holder at '{address}' is unreachable (failover takes up to the "
                + "lease duration; the address refreshes each renew interval). Retry shortly.");
            return;
        }

        using (upstreamResponse) {
            context.Response.StatusCode = (int)upstreamResponse.StatusCode;
            foreach (var header in upstreamResponse.Headers.Concat(upstreamResponse.Content.Headers)) {
                if (!HopByHopHeaders.Contains(header.Key, StringComparer.OrdinalIgnoreCase)) {
                    context.Response.Headers[header.Key] = header.Value.ToArray();
                }
            }

            // Copy with a flush per read so streaming responses (SSE) flow through instead of pooling
            // in buffers. Only the proxy path pays this; the holder serves directly.
            var upstreamBody = await upstreamResponse.Content.ReadAsStreamAsync(context.RequestAborted);
            var buffer = new byte[8192];
            try {
                int read;
                while ((read = await upstreamBody.ReadAsync(buffer, context.RequestAborted)) > 0) {
                    await context.Response.Body.WriteAsync(buffer.AsMemory(0, read), context.RequestAborted);
                    await context.Response.Body.FlushAsync(context.RequestAborted);
                }
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) {
                // The caller disconnected mid-stream (normal for SSE); stop copying.
            }
        }
    }

    private static async Task Reject(HttpContext context, string message) {
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        context.Response.Headers.RetryAfter = "5";
        context.Response.ContentType = "text/plain; charset=utf-8";
        await context.Response.WriteAsync(message, context.RequestAborted);
    }
}
