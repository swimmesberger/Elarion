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
internal readonly record struct RoleHolderTarget(string Role, bool IsHeld, string? CurrentHolderAddress);

internal sealed class RoleHolderProxyMiddleware {
    private readonly Func<HttpContext, RoleHolderTarget?> _resolveTarget;
    private readonly PathString[] _pathPrefixes;
    private readonly HttpMessageInvoker _client;
    private readonly ILogger _logger;

    /// <summary>Marks a forwarded request so a mid-failover receiver answers 503 instead of re-forwarding.</summary>
    public const string ProxiedHeaderName = "Elarion-Role-Proxied";

    /// <summary>
    /// How long the proxy waits for a TCP/TLS connection to the holder. A crashed node or dropped SYNs would
    /// otherwise hang forever (<see cref="System.Net.Http.SocketsHttpHandler.ConnectTimeout"/> defaults to
    /// infinite) instead of answering the documented 503 + Retry-After.
    /// </summary>
    public static readonly TimeSpan DefaultConnectTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long the proxy waits for the holder's response <em>headers</em> (connect + send + first response
    /// bytes). Generous — the holder runs real handlers — but bounded, so a black-holed holder surfaces as the
    /// documented 503 instead of an indefinite hang. Only time-to-headers is bounded; a streaming body (SSE)
    /// flows for as long as the caller stays connected.
    /// </summary>
    public static readonly TimeSpan DefaultResponseHeadersTimeout = TimeSpan.FromSeconds(30);

    private readonly TimeSpan _responseHeadersTimeout;

    public RoleHolderProxyMiddleware(
        IRoleLease lease,
        PathString[] pathPrefixes,
        HttpMessageInvoker client,
        ILogger logger,
        TimeSpan? responseHeadersTimeout = null)
        : this(
            _ => new RoleHolderTarget(lease.Role, lease.IsHeld, lease.CurrentHolderAddress),
            pathPrefixes,
            client,
            logger,
            responseHeadersTimeout) {
    }

    public RoleHolderProxyMiddleware(
        Func<HttpContext, RoleHolderTarget?> resolveTarget,
        PathString[] pathPrefixes,
        HttpMessageInvoker client,
        ILogger logger,
        TimeSpan? responseHeadersTimeout = null) {
        _resolveTarget = resolveTarget;
        _pathPrefixes = pathPrefixes;
        _client = client;
        _logger = logger;
        _responseHeadersTimeout = responseHeadersTimeout ?? DefaultResponseHeadersTimeout;
    }

    // Hop-by-hop headers never cross a proxy (RFC 9110 §7.6.1); Host is set from the target. Headers the
    // message's own Connection header nominates are hop-by-hop too and are stripped per message.
    private static readonly string[] HopByHopHeaders = [
        "Connection", "Keep-Alive", "Proxy-Authenticate", "Proxy-Authorization",
        "Proxy-Connection", "TE", "Trailer", "Transfer-Encoding", "Upgrade", "Host"
    ];

    public async Task InvokeAsync(HttpContext context, RequestDelegate next) {
        var path = context.Request.Path;
        var matches = false;
        foreach (var prefix in _pathPrefixes)
            if (path.StartsWithSegments(prefix)) {
                matches = true;
                break;
            }

        if (!matches) {
            await next(context);
            return;
        }

        var target = _resolveTarget(context);
        if (target is null) {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync(
                "The request does not contain a valid role-partition affinity key.",
                context.RequestAborted);
            return;
        }

        if (target.Value.IsHeld) {
            await next(context);
            return;
        }

        if (context.Request.Headers.ContainsKey(ProxiedHeaderName)) {
            // Already forwarded once and this instance still isn't the holder: the lease is mid-failover.
            // Never re-forward — one hop, bounded.
            await Reject(context, $"The '{target.Value.Role}' role lease is moving between instances; retry shortly.");
            return;
        }

        var address = target.Value.CurrentHolderAddress;
        if (address is null) {
            await Reject(
                context,
                $"The '{target.Value.Role}' role holder is unknown or does not advertise an address. Register "
                + "AddElarionInstanceAddress() (or set RoleLeaseOptions.AdvertisedAddress) on every instance.");
            return;
        }

        await ProxyAsync(context, target.Value.Role, address);
    }

    private async Task ProxyAsync(HttpContext context, string role, string address) {
        var request = context.Request;
        var targetUri = new Uri(
            address.TrimEnd('/') + request.PathBase + request.Path + request.QueryString,
            UriKind.Absolute);

        using var upstreamRequest = new HttpRequestMessage(new HttpMethod(request.Method), targetUri);
        if (request.ContentLength > 0 || request.Headers.ContainsKey("Transfer-Encoding"))
            upstreamRequest.Content = new StreamContent(request.Body);

        var requestNominated = NominatedConnectionHeaders(request.Headers.Connection);
        foreach (var header in request.Headers) {
            if (IsHopByHop(header.Key, requestNominated)) continue;

            if (!upstreamRequest.Headers.TryAddWithoutValidation(header.Key, (IEnumerable<string?>)header.Value))
                upstreamRequest.Content?.Headers.TryAddWithoutValidation(header.Key,
                    (IEnumerable<string?>)header.Value);
        }

        upstreamRequest.Headers.TryAddWithoutValidation(ProxiedHeaderName, role);

        HttpResponseMessage upstreamResponse;
        // Bounds time-to-response-headers (SocketsHttpHandler has no such knob and HttpMessageInvoker no default
        // timeout); the registration disposes with the linked source, so a long-lived streaming body is unaffected.
        using var headersTimeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        headersTimeout.CancelAfter(_responseHeadersTimeout);
        try {
            // HttpMessageInvoker streams: the response returns after headers, bodies flow through.
            upstreamResponse = await _client.SendAsync(upstreamRequest, headersTimeout.Token);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) {
            return; // the caller went away; nothing to answer
        }
        catch (OperationCanceledException) {
            // Our own headers deadline fired: the holder accepted (or black-holed) the connection but never
            // answered — same bounded failure contract as an unreachable holder.
            _logger.LogWarning(
                "Proxying to the '{Role}' role holder at {Address} timed out after {Timeout}.",
                role, address, _responseHeadersTimeout);
            await Reject(
                context,
                $"The '{role}' role holder at '{address}' did not respond within "
                + $"{_responseHeadersTimeout.TotalSeconds:0.###} s. Retry shortly.");
            return;
        }
        catch (HttpRequestException ex) {
            // Covers connection failures including SocketsHttpHandler's ConnectTimeout (a crashed node or
            // dropped SYNs no longer hangs — the handler is configured with DefaultConnectTimeout).
            _logger.LogWarning(
                ex, "Proxying to the '{Role}' role holder at {Address} failed.", role, address);
            await Reject(
                context,
                $"The '{role}' role holder at '{address}' is unreachable (failover takes up to the "
                + "lease duration; the address refreshes each renew interval). Retry shortly.");
            return;
        }

        using (upstreamResponse) {
            context.Response.StatusCode = (int)upstreamResponse.StatusCode;
            var responseNominated = upstreamResponse.Headers.TryGetValues("Connection", out var connectionValues)
                ? NominatedConnectionHeaders(connectionValues)
                : null;
            foreach (var header in upstreamResponse.Headers.Concat(upstreamResponse.Content.Headers))
                if (!IsHopByHop(header.Key, responseNominated))
                    context.Response.Headers[header.Key] = header.Value.ToArray();

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

    private static bool IsHopByHop(string name, HashSet<string>? nominated) {
        return HopByHopHeaders.Contains(name, StringComparer.OrdinalIgnoreCase)
               || nominated?.Contains(name) == true;
    }

    // RFC 9110 §7.6.1: the Connection header nominates additional per-message hop-by-hop headers; an
    // intermediary must strip the nominated headers along with Connection itself.
    private static HashSet<string>? NominatedConnectionHeaders(IEnumerable<string?> connectionValues) {
        HashSet<string>? nominated = null;
        foreach (var value in connectionValues) {
            if (string.IsNullOrEmpty(value)) continue;

            foreach (var token in value.Split(',')) {
                var name = token.Trim();
                if (name.Length > 0) {
                    nominated ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    nominated.Add(name);
                }
            }
        }

        return nominated;
    }

    private static async Task Reject(HttpContext context, string message) {
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        context.Response.Headers.RetryAfter = "5";
        context.Response.ContentType = "text/plain; charset=utf-8";
        await context.Response.WriteAsync(message, context.RequestAborted);
    }
}
