using Microsoft.Extensions.DependencyInjection;

namespace Elarion.AspNetCore;

/// <summary>
/// Executes JSON-RPC batch requests sequentially, one at a time.
/// Each request gets its own DI scope (and therefore its own
/// <c>DbContext</c> and transaction via pipeline behaviors).
/// </summary>
/// <remarks>
/// This is the simplest and safest strategy: no deadlock risk, predictable
/// ordering, and low memory usage. For higher throughput on read-heavy batches,
/// swap to a bounded-parallelism implementation via DI registration.
/// </remarks>
public sealed class SequentialBatchStrategy : IBatchExecutionStrategy {
    /// <inheritdoc />
    public async Task<List<JsonRpcResponse>> ExecuteAsync(
        IReadOnlyList<JsonRpcRequest> requests,
        JsonRpcDispatcher dispatcher,
        IServiceProvider rootProvider,
        CancellationToken ct) {
        var responses = new List<JsonRpcResponse>(requests.Count);

        foreach (var request in requests) {
            await using var scope = rootProvider.CreateAsyncScope();
            var response = await dispatcher.DispatchAsync(
                request, scope.ServiceProvider, ct);

            // Per JSON-RPC 2.0 spec §6: notifications (no id) produce no response
            if (request.Id is not null) {
                responses.Add(response);
            }
        }

        return responses;
    }
}
