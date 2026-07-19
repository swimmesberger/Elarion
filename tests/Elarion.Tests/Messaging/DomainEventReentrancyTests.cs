using AwesomeAssertions;
using Elarion.Abstractions.Messaging;
using Elarion.Messaging;
using Elarion.Messaging.InMemory;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Elarion.Tests.Messaging;

/// <summary>
/// A domain consumer that (transitively) re-publishes its own event recurses inline, since Plane A
/// dispatches consumers on the caller's stack. Without a depth guard a publish cycle overflows the stack
/// (an unhandleable crash); the bus must fail the publish with a clear exception instead.
/// </summary>
public sealed class DomainEventReentrancyTests {
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task PublishAsync_SelfRepublishingConsumer_FailsWithInvalidOperationRatherThanStackOverflow() {
        using var cts = new CancellationTokenSource(WaitTimeout);
        await using var provider = BuildProvider(RepublishingSubscriber(typeof(SelfEvent)));

        using var scope = provider.CreateScope();
        var bus = scope.ServiceProvider.GetRequiredService<IDomainEventBus>();

        var act = async () => await bus.PublishAsync(new SelfEvent(), cts.Token);

        var ex = (await act.Should().ThrowAsync<InvalidOperationException>()).Which;
        ex.Message.Should().Contain(nameof(SelfEvent));
        ex.Message.Should().Contain("re-entrancy depth");
    }

    [Fact]
    public async Task PublishAsync_BoundedNesting_UnderLimit_Succeeds() {
        using var cts = new CancellationTokenSource(WaitTimeout);
        var recorder = new Counter();
        await using var provider = BuildProvider(BoundedSubscriber(typeof(SelfEvent), recorder, 5));

        using var scope = provider.CreateScope();
        var bus = scope.ServiceProvider.GetRequiredService<IDomainEventBus>();

        await bus.PublishAsync(new SelfEvent(), cts.Token);

        // Depth 5 (< limit of 32) completes cleanly.
        recorder.Value.Should().Be(5);
    }

    private static ServiceProvider BuildProvider(EventSubscriptionDescriptor descriptor) {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(descriptor);
        services.AddElarionDomainEventBus();
        services.AddElarionInMemoryIntegrationEventBus();
        return services.BuildServiceProvider();
    }

    private static EventSubscriptionDescriptor RepublishingSubscriber(Type eventType) {
        return new EventSubscriptionDescriptor {
            EventType = eventType,
            Plane = EventPlane.Domain,
            ServiceType = typeof(SelfEvent),
            Order = 0,
            InvokeAsync = async (sp, evt, _, ct) => {
                var bus = sp.GetRequiredService<IDomainEventBus>();
                await bus.PublishAsync((SelfEvent)evt, ct).ConfigureAwait(false);
            }
        };
    }

    private static EventSubscriptionDescriptor BoundedSubscriber(Type eventType, Counter counter, int maxDepth) {
        return new EventSubscriptionDescriptor {
            EventType = eventType,
            Plane = EventPlane.Domain,
            ServiceType = typeof(SelfEvent),
            Order = 0,
            InvokeAsync = async (sp, evt, _, ct) => {
                if (counter.Increment() >= maxDepth) return;

                var bus = sp.GetRequiredService<IDomainEventBus>();
                await bus.PublishAsync((SelfEvent)evt, ct).ConfigureAwait(false);
            }
        };
    }

    private sealed record SelfEvent : IDomainEvent;

    private sealed class Counter {
        private int _value;
        public int Value => _value;

        public int Increment() {
            return Interlocked.Increment(ref _value);
        }
    }
}
