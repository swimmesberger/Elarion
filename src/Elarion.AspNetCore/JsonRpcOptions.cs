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
}
