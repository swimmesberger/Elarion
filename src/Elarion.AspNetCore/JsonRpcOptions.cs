using System.Text.Json;

namespace Elarion.AspNetCore;

/// <summary>
/// Configuration options for the JSON-RPC 2.0 endpoint.
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
    /// Pre-built <see cref="JsonSerializerOptions"/> to use for serialising and deserialising
    /// JSON-RPC requests and responses. When set, the library registers this instance directly
    /// as the DI singleton and calls <see cref="JsonSerializerOptions.MakeReadOnly"/> on it to
    /// prevent accidental mutation after startup.
    /// <para>
    /// When <see langword="null"/> (the default), a minimal options instance is built
    /// automatically with camelCase naming and the built-in <see cref="JsonRpcJsonContext"/>
    /// resolver for envelope types.
    /// </para>
    /// </summary>
    public JsonSerializerOptions? SerializerOptions { get; set; }
}
