using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Elarion.Abstractions;
using Elarion.Abstractions.Resilience;
using Elarion.Resilience;
using Xunit;

namespace Elarion.Tests.Services;

public sealed class ResilienceDecoratorTests {
    [Fact]
    public void AddElarionResilience_NoPipelines_RegistersPipelineRunnerDependencies() {
        var services = new ServiceCollection();
        services.AddElarionResilience();
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions {
            ValidateOnBuild = true,
            ValidateScopes = true
        });

        provider.GetRequiredService<IResiliencePipelineRunner>().Should().NotBeNull();
    }

    [Fact]
    public async Task ExecuteAsync_RetryPolicy_RetriesFailedOperation() {
        var services = new ServiceCollection();
        services.AddElarionResiliencePolicyMetadata(new ResiliencePolicyMetadata {
            Name = "test-retry",
            Retry = new ResilienceRetryOptions {
                MaxRetryAttempts = 2,
                Delay = TimeSpan.Zero,
                Backoff = ResilienceBackoffType.Constant
            }
        });
        services.AddElarionResilience();
        await using var provider = services.BuildServiceProvider();
        var runner = provider.GetRequiredService<IResiliencePipelineRunner>();
        var attempts = 0;

        await runner.ExecuteAsync(
            new ResiliencePolicyReference { Name = "test-retry" },
            _ => {
                attempts++;
                if (attempts < 3) {
                    throw new InvalidOperationException("retry");
                }

                return ValueTask.CompletedTask;
            },
            TestContext.Current.CancellationToken);

        attempts.Should().Be(3);
    }

    [Fact]
    public async Task HandleAsync_ResilienceDecorator_RetriesInnerHandler() {
        var services = new ServiceCollection();
        services.AddElarionResiliencePolicyMetadata(new ResiliencePolicyMetadata {
            Name = "handler-retry",
            Retry = new ResilienceRetryOptions {
                MaxRetryAttempts = 1,
                Delay = TimeSpan.Zero,
                Backoff = ResilienceBackoffType.Constant
            }
        });
        services.AddElarionResilience();
        await using var provider = services.BuildServiceProvider();
        var inner = new FlakyHandler();
        var decorator = new ResilienceDecorator<Request, int>(
            inner,
            provider.GetRequiredService<IResiliencePipelineRunner>(),
            new ResiliencePolicyReference { Name = "handler-retry" });

        var result = await decorator.HandleAsync(
            new Request(),
            TestContext.Current.CancellationToken);

        result.Should().Be(42);
        inner.Attempts.Should().Be(2);
    }

    [Fact]
    public async Task ExecuteAsync_RetryPolicy_EmitsTraceSpanAndRetryEvent() {
        using var activities = new ActivityCollector(ResilienceTelemetry.ActivitySourceName);
        using var meters = new MeterCollector(ResilienceTelemetry.MeterName);
        var services = new ServiceCollection();
        services.AddElarionResiliencePolicyMetadata(new ResiliencePolicyMetadata {
            Name = "telemetry-retry",
            Retry = new ResilienceRetryOptions {
                MaxRetryAttempts = 1,
                Delay = TimeSpan.Zero,
                Backoff = ResilienceBackoffType.Constant
            }
        });
        services.AddElarionResilience();
        await using var provider = services.BuildServiceProvider();
        var runner = provider.GetRequiredService<IResiliencePipelineRunner>();
        var attempts = 0;

        await runner.ExecuteAsync(
            new ResiliencePolicyReference { Name = "telemetry-retry" },
            _ => {
                attempts++;
                if (attempts == 1) {
                    throw new InvalidOperationException("retry");
                }

                return ValueTask.CompletedTask;
            },
            TestContext.Current.CancellationToken);

        activities.Activities.Should().Contain(activity =>
            activity.DisplayName == "resilience telemetry-retry" &&
            Equals(activity.GetTag("resilience.policy.outcome"), "success") &&
            activity.Events.Any(evt => evt.Name == "resilience retry"));
        meters.Measurements.Should().Contain(measurement =>
            measurement.InstrumentName == "resilience.policy.execution.count" &&
            measurement.HasTag("resilience.policy.name", "telemetry-retry") &&
            measurement.HasTag("resilience.policy.outcome", "success"));
    }

    private sealed record Request;

    private sealed class FlakyHandler : IHandler<Request, int> {
        public int Attempts { get; private set; }

        public ValueTask<int> HandleAsync(Request request, CancellationToken ct) {
            Attempts++;
            if (Attempts == 1) {
                throw new InvalidOperationException("retry");
            }

            return ValueTask.FromResult(42);
        }
    }
}
