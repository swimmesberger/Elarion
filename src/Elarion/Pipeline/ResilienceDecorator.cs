using Elarion.Abstractions;
using Elarion.Abstractions.Resilience;

namespace Elarion.Pipeline;

/// <summary>
/// Wraps a handler invocation in a named resilience policy.
/// </summary>
public sealed class ResilienceDecorator<TRequest, TResponse>(
    IHandler<TRequest, TResponse> inner,
    IResiliencePipelineRunner runner,
    ResiliencePolicyReference policy
) : IHandler<TRequest, TResponse> {
    /// <inheritdoc />
    // Note 25: The decorator retries the entire inner handler pipeline, not just the handler method body.
    public async ValueTask<TResponse> HandleAsync(TRequest request, CancellationToken ct) =>
        // Note 26: The runner receives a delegate so the selected resilience runtime can call it once or many times.
        await runner.ExecuteAsync(policy, token => inner.HandleAsync(request, token), ct);
}
