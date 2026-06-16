using Elarion.JsonRpc;

namespace Elarion.AspNetCore;

/// <summary>
/// Defines how individual requests within a JSON-RPC 2.0 batch are executed.
/// Implementations can vary execution order and concurrency while ensuring
/// each request is dispatched in its own DI scope.
/// </summary>
/// <example>
/// <code>
/// // Register the default sequential strategy:
/// builder.Services.AddSingleton&lt;IBatchExecutionStrategy, SequentialBatchStrategy&gt;();
///
/// // Swap to bounded parallelism later:
/// builder.Services.AddSingleton&lt;IBatchExecutionStrategy, BoundedParallelBatchStrategy&gt;();
/// </code>
/// </example>
public interface IBatchExecutionStrategy {
    /// <summary>
    /// Executes a batch of JSON-RPC 2.0 requests and returns their responses.
    /// Per the JSON-RPC 2.0 spec, each request is independent — failure of one
    /// must not affect others. Notifications are requests where the <c>id</c>
    /// member is absent; explicit <c>"id": null</c> requests and invalid batch
    /// envelopes still require a response item. Custom strategies should use
    /// <see cref="JsonRpcRequest.ShouldSendResponse"/> for this decision.
    /// </summary>
    /// <param name="requests">The parsed batch of JSON-RPC requests.</param>
    /// <param name="dispatcher">The RPC dispatcher to invoke for each request.</param>
    /// <param name="jsonOptions">JSON serializer options for request/response handling.</param>
    /// <param name="rootProvider">
    /// The root <see cref="IServiceProvider"/> from which per-request scopes are created.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// A list of JSON-RPC responses, one per non-notification request.
    /// The order matches the request order for sequential strategies,
    /// but the spec allows any order.
    /// </returns>
    Task<List<JsonRpcResponse>> ExecuteAsync(
        IReadOnlyList<JsonRpcRequest> requests,
        JsonRpcDispatcher dispatcher,
        IServiceProvider rootProvider,
        CancellationToken ct);
}
