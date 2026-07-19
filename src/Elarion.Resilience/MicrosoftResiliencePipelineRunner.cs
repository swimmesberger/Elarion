using System.Collections.Concurrent;
using System.Diagnostics;
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
        using var activity = StartActivity(policy);
        var started = Stopwatch.GetTimestamp();
        var outcome = "success";
        try {
            // The framework stores neutral policy metadata; the default runtime lazily compiles Microsoft/Polly pipelines from it.
            var pipeline = GetPipeline(policy);
            await pipeline.ExecuteAsync(action, ct);
        }
        catch (Exception ex) {
            outcome = ex is OperationCanceledException ? "cancelled" : "failed";
            RecordException(activity, ex);
            throw;
        }
        finally {
            RecordOutcome(activity, policy.Name, outcome, started);
        }
    }

    public async ValueTask<T> ExecuteAsync<T>(
        ResiliencePolicyReference policy,
        Func<CancellationToken, ValueTask<T>> action,
        CancellationToken ct) {
        using var activity = StartActivity(policy);
        var started = Stopwatch.GetTimestamp();
        var outcome = "success";
        try {
            // This overload keeps typed handler responses type-safe while sharing the same named pipeline lookup.
            var pipeline = GetPipeline(policy);
            return await pipeline.ExecuteAsync(action, ct);
        }
        catch (Exception ex) {
            outcome = ex is OperationCanceledException ? "cancelled" : "failed";
            RecordException(activity, ex);
            throw;
        }
        finally {
            RecordOutcome(activity, policy.Name, outcome, started);
        }
    }

    private ResiliencePipeline GetPipeline(ResiliencePolicyReference policy) {
        return _pipelines.GetOrAdd(policy.Name, name => BuildPipeline(
            policies.GetPolicy(policy) ??
            throw new InvalidOperationException($"Resilience policy '{name}' is not registered.")));
    }

    private static ResiliencePipeline BuildPipeline(ResiliencePolicyMetadata metadata) {
        var builder = new ResiliencePipelineBuilder();

        if (metadata.Retry is { } retry)
            builder.AddRetry(new RetryStrategyOptions {
                ShouldHandle = new PredicateBuilder().Handle<Exception>(static exception =>
                    exception is not OperationCanceledException &&
                    exception is not NonRetryableException),
                MaxRetryAttempts = retry.MaxRetryAttempts,
                Delay = retry.Delay,
                BackoffType = ToMicrosoftBackoff(retry.Backoff),
                MaxDelay = retry.MaxDelay,
                UseJitter = retry.UseJitter,
                OnRetry = static args => {
                    var tags = new ActivityTagsCollection {
                        { "resilience.retry.attempt", args.AttemptNumber + 1 },
                        { "resilience.retry.delay_ms", args.RetryDelay.TotalMilliseconds }
                    };
                    if (args.Outcome.Exception is { } exception)
                        tags.Add("exception.type", exception.GetType().FullName);

                    Activity.Current?.AddEvent(new ActivityEvent("resilience retry", tags: tags));
                    return default;
                }
            });

        if (metadata.Timeout is { } timeout)
            builder.AddTimeout(new TimeoutStrategyOptions {
                Timeout = timeout,
                OnTimeout = static args => {
                    Activity.Current?.AddEvent(new ActivityEvent("resilience timeout", tags: new ActivityTagsCollection {
                        { "resilience.timeout_ms", args.Timeout.TotalMilliseconds }
                    }));
                    return default;
                }
            });

        return builder.Build();
    }

    private static DelayBackoffType ToMicrosoftBackoff(ResilienceBackoffType backoff) {
        return backoff switch {
            ResilienceBackoffType.Constant => DelayBackoffType.Constant,
            ResilienceBackoffType.Linear => DelayBackoffType.Linear,
            ResilienceBackoffType.Exponential => DelayBackoffType.Exponential,
            _ => throw new ArgumentOutOfRangeException(nameof(backoff), backoff, "Unknown resilience backoff type.")
        };
    }

    private static Activity? StartActivity(ResiliencePolicyReference policy) {
        // Guard before interpolating: with no listener the name string would still be built each call.
        if (!ResilienceTelemetry.Source.HasListeners()) return null;

        var activity = ResilienceTelemetry.Source.StartActivity(
            $"resilience {policy.Name}",
            ActivityKind.Internal);
        if (activity?.IsAllDataRequested == true) activity.SetTag("resilience.policy.name", policy.Name);

        return activity;
    }

    private static void RecordOutcome(Activity? activity, string policyName, string outcome, long started) {
        if (activity?.IsAllDataRequested == true) {
            activity.SetTag("resilience.policy.outcome", outcome);
            if (outcome != "success") activity.SetStatus(ActivityStatusCode.Error, outcome);
        }

        ResilienceTelemetry.RecordExecution(
            policyName,
            outcome,
            Stopwatch.GetElapsedTime(started));
    }

    private static void RecordException(Activity? activity, Exception exception) {
        activity?.AddEvent(new ActivityEvent("exception", tags: new ActivityTagsCollection {
            { "exception.type", exception.GetType().FullName },
            { "exception.message", exception.Message }
        }));
        activity?.SetStatus(ActivityStatusCode.Error, exception.Message);
    }
}
