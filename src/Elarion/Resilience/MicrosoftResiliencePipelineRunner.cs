using System.Collections.Concurrent;
using Polly;
using Polly.Retry;
using Polly.Timeout;
using Elarion.Abstractions.Resilience;

namespace Elarion.Resilience;

internal sealed class MicrosoftResiliencePipelineRunner(
    IResiliencePolicyCatalog policies
) : IResiliencePipelineRunner {
    private readonly ConcurrentDictionary<string, ResiliencePipeline> _pipelines = new(StringComparer.Ordinal);

    public async ValueTask ExecuteAsync(
        ResiliencePolicyReference policy,
        Func<CancellationToken, ValueTask> action,
        CancellationToken ct) {
        // Note 27: The framework stores neutral policy metadata; the default runtime lazily compiles Microsoft/Polly pipelines from it.
        var pipeline = GetPipeline(policy);
        await pipeline.ExecuteAsync(action, ct);
    }

    public async ValueTask<T> ExecuteAsync<T>(
        ResiliencePolicyReference policy,
        Func<CancellationToken, ValueTask<T>> action,
        CancellationToken ct) {
        // Note 28: This overload keeps typed handler responses type-safe while sharing the same named pipeline lookup.
        var pipeline = GetPipeline(policy);
        return await pipeline.ExecuteAsync(action, ct);
    }

    private ResiliencePipeline GetPipeline(ResiliencePolicyReference policy) =>
        _pipelines.GetOrAdd(policy.Name, name => BuildPipeline(
            policies.GetPolicy(policy) ??
            throw new InvalidOperationException($"Resilience policy '{name}' is not registered.")));

    private static ResiliencePipeline BuildPipeline(ResiliencePolicyMetadata metadata) {
        var builder = new ResiliencePipelineBuilder();

        if (metadata.Retry is { } retry) {
            builder.AddRetry(new RetryStrategyOptions {
                ShouldHandle = new PredicateBuilder().Handle<Exception>(
                    static exception => exception is not OperationCanceledException &&
                        exception is not NonRetryableException),
                MaxRetryAttempts = retry.MaxRetryAttempts,
                Delay = retry.Delay,
                BackoffType = ToMicrosoftBackoff(retry.Backoff),
                MaxDelay = retry.MaxDelay,
                UseJitter = retry.UseJitter
            });
        }

        if (metadata.Timeout is { } timeout) {
            builder.AddTimeout(new TimeoutStrategyOptions {
                Timeout = timeout
            });
        }

        return builder.Build();
    }

    private static DelayBackoffType ToMicrosoftBackoff(ResilienceBackoffType backoff) =>
        backoff switch {
            ResilienceBackoffType.Constant => DelayBackoffType.Constant,
            ResilienceBackoffType.Linear => DelayBackoffType.Linear,
            ResilienceBackoffType.Exponential => DelayBackoffType.Exponential,
            _ => throw new ArgumentOutOfRangeException(nameof(backoff), backoff, "Unknown resilience backoff type.")
        };
}
