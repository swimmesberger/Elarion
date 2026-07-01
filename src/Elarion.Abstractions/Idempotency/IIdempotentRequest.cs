namespace Elarion.Abstractions.Idempotency;

/// <summary>
/// Optional in-band bridge: a request that carries its own idempotency key on the DTO (AIP-155
/// <c>request_id</c> style). The decorator prefers a key seeded from the transport boundary (HTTP header,
/// JSON-RPC/MCP <c>_meta</c>) but falls back to this field — the natural key source for event consumers and
/// per-message batch keys, where there is no transport-level metadata.
/// </summary>
public interface IIdempotentRequest {
    /// <summary>The idempotency key for this request, or <see langword="null"/> when none is carried in-band.</summary>
    string? IdempotencyKey { get; }
}
