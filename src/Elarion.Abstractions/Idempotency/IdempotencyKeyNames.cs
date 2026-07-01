namespace Elarion.Abstractions.Idempotency;

/// <summary>
/// The well-known names each transport uses to carry an idempotency key, shared so every transport captures the
/// key into the same dispatch-scope rail.
/// </summary>
public static class IdempotencyKeyNames {
    /// <summary>The standard HTTP request header (IETF <c>draft-ietf-httpapi-idempotency-key-header</c>, Stripe).</summary>
    public const string HttpHeader = "Idempotency-Key";

    /// <summary>The legacy HTTP header some clients still send; accepted as an alias for <see cref="HttpHeader"/>.</summary>
    public const string LegacyHttpHeader = "X-Idempotency-Key";

    /// <summary>
    /// The reverse-DNS key under a JSON-RPC/MCP request's <c>params._meta</c> object (MCP's own metadata
    /// convention; a framework-owned prefix, deliberately not the reserved <c>mcp</c>/<c>modelcontextprotocol</c>).
    /// </summary>
    public const string MetaKey = "dev.wimmesberger.elarion/idempotencyKey";
}
