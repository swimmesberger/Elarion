namespace Elarion.AspNetCore;

/// <summary>
/// Configuration options for the JSON-RPC 2.0 endpoint. JSON serialization is configured centrally through the
/// canonical <c>IElarionJsonSerialization</c> options (via <c>ConfigureElarionJson</c>), not here.
/// </summary>
public sealed class JsonRpcOptions {
    /// <summary>
    /// Maximum number of requests allowed in a single batch.
    /// Prevents abuse and resource exhaustion from oversized batches.
    /// Default is 20.
    /// </summary>
    public int MaxBatchSize { get; set; } = 20;

    /// <summary>
    /// The HTTP path at which the JSON-RPC endpoint is mapped.
    /// Default is <c>/rpc</c>.
    /// </summary>
    public string EndpointPath { get; set; } = "/rpc";

    /// <summary>
    /// Whether the endpoint refuses a request whose <c>Content-Type</c> is not JSON (<c>application/json</c> or an
    /// <c>application/*+json</c> type) with HTTP 415 and a JSON-RPC <c>invalid_request</c> envelope, before reading
    /// the body. Default is <see langword="true"/>.
    /// </summary>
    /// <remarks>
    /// This is a CSRF defense for hosts that authenticate browsers with a cookie. A cross-site
    /// <c>&lt;form enctype="text/plain"&gt;</c> post, or a <c>no-cors</c> <c>fetch</c> with a string or untyped
    /// body, is a CORS "simple" request: the browser sends it without a preflight and attaches the site's cookies.
    /// Requiring a JSON content type makes every cross-origin call a preflighted request, which the browser only
    /// sends when the host's CORS policy allows that origin. A request with no <c>Content-Type</c> is refused too.
    /// Set this to <see langword="false"/> only for a host whose <c>/rpc</c> callers cannot send the header and
    /// that does not authenticate with a credential the browser attaches on its own (a cookie, cached HTTP
    /// Basic/Negotiate credentials, or a client certificate).
    /// </remarks>
    public bool RequireJsonContentType { get; set; } = true;
}
