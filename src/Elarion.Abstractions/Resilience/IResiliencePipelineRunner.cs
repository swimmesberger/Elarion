namespace Elarion.Abstractions.Resilience;

/// <summary>
/// Executes delegates through named resilience policies.
/// </summary>
public interface IResiliencePipelineRunner {
    /// <summary>Executes an async operation through the named resilience policy.</summary>
    ValueTask ExecuteAsync(
        ResiliencePolicyReference policy,
        Func<CancellationToken, ValueTask> action,
        CancellationToken ct);

    /// <summary>Executes an async operation with a result through the named resilience policy.</summary>
    ValueTask<T> ExecuteAsync<T>(
        ResiliencePolicyReference policy,
        Func<CancellationToken, ValueTask<T>> action,
        CancellationToken ct);
}
