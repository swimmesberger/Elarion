using System.Diagnostics;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using Elarion.Abstractions.Messaging;
using Elarion.Abstractions.Serialization;
using Elarion.Messaging.Outbox;
using Microsoft.Extensions.DependencyInjection;

namespace Elarion.Benchmarks.Messaging;

/// <summary>
/// Measures the synchronous request-path work of publishing an outbox event before <c>SaveChanges</c>.
/// <c>LegacySingleRow</c> is the previous message-only shape; <c>RoleAffine</c> adds the independently
/// persisted consumer deliveries. PostgreSQL I/O is deliberately excluded: the benchmark isolates the
/// framework CPU/allocation delta while integration tests cover the relational mappings and claim path.
///
///   dotnet run --project tests/Elarion.Benchmarks -c Release -- --filter "*OutboxPublish*"
/// </summary>
[MemoryDiagnoser]
public class OutboxPublishBenchmarks {
    [Params(1, 3)]
    public int ConsumerCount { get; set; }

    private readonly OutboxBenchmarkEvent _event = new(7, "alice");
    private CapturingStore _legacyStore = null!;
    private CapturingStore _roleAffineStore = null!;
    private OutboxIntegrationEventBus _roleAffineBus = null!;
    private IElarionJsonSerialization _json = null!;

    [GlobalSetup]
    public void Setup() {
        _json = new ServiceCollection()
            .ConfigureElarionJson(options => options.EnableReflectionFallback = true)
            .BuildServiceProvider()
            .GetRequiredService<IElarionJsonSerialization>();
        _legacyStore = new CapturingStore();
        _roleAffineStore = new CapturingStore();

        var descriptors = Enumerable.Range(0, ConsumerCount)
            .Select(index => Descriptor($"consumer-{index}"))
            .ToArray();
        _roleAffineBus = new OutboxIntegrationEventBus(
            _roleAffineStore,
            new OutboxConsumerCatalog(descriptors),
            EmptyProvider.Instance,
            new OutboxOptions(),
            _json,
            TimeProvider.System);
    }

    [Benchmark(Baseline = true)]
    public OutboxMessage LegacySingleRow() {
        var message = new OutboxMessage {
            Id = Guid.CreateVersion7(),
            OccurredOnUtc = TimeProvider.System.GetUtcNow(),
            EventType = typeof(OutboxBenchmarkEvent).FullName!,
            Payload = JsonSerializer.Serialize(_event, _json.Options),
            CorrelationId = Guid.CreateVersion7(),
            TraceParent = Activity.Current?.Id
        };
        _legacyStore.Append(message);
        return message;
    }

    [Benchmark]
    public OutboxMessage RoleAffine() {
        _roleAffineBus.PublishAsync(_event).GetAwaiter().GetResult();
        return _roleAffineStore.Last!;
    }

    private static EventSubscriptionDescriptor Descriptor(string consumerId) => new() {
        ConsumerId = consumerId,
        EventType = typeof(OutboxBenchmarkEvent),
        Plane = EventPlane.Integration,
        ServiceType = typeof(OutboxPublishBenchmarks),
        InvokeAsync = static (_, _, _, _) => ValueTask.CompletedTask
    };
}

/// <summary>
/// Isolates the descriptor discovery optimization: the former LINQ scan/sort/validation ran on every
/// publish, while the catalog arm is the production dictionary lookup built once at startup.
/// </summary>
[MemoryDiagnoser]
public class OutboxConsumerLookupBenchmarks {
    [Params(16, 128)]
    public int DescriptorCount { get; set; }

    private EventSubscriptionDescriptor[] _descriptors = null!;
    private OutboxConsumerCatalog _catalog = null!;

    [GlobalSetup]
    public void Setup() {
        _descriptors = Enumerable.Range(0, DescriptorCount)
            .Select(index => new EventSubscriptionDescriptor {
                ConsumerId = $"consumer-{index}",
                EventType = index == 0 ? typeof(OutboxBenchmarkEvent) : typeof(UnrelatedBenchmarkEvent),
                Plane = EventPlane.Integration,
                ServiceType = typeof(OutboxConsumerLookupBenchmarks),
                Order = index,
                InvokeAsync = static (_, _, _, _) => ValueTask.CompletedTask
            })
            .ToArray();
        _catalog = new OutboxConsumerCatalog(_descriptors);
    }

    [Benchmark(Baseline = true)]
    public int LegacyPerPublishScan() {
        var consumers = _descriptors
            .Where(descriptor => descriptor.Plane is EventPlane.Integration
                && descriptor.EventType == typeof(OutboxBenchmarkEvent)
                && descriptor.InvokeAsync is not null)
            .OrderBy(descriptor => descriptor.Order)
            .ToArray();
        var duplicate = consumers
            .Where(descriptor => !string.IsNullOrWhiteSpace(descriptor.ConsumerId))
            .GroupBy(descriptor => descriptor.ConsumerId, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        var missingId = consumers.FirstOrDefault(descriptor => string.IsNullOrWhiteSpace(descriptor.ConsumerId));
        return duplicate is null && missingId is null ? consumers.Length : -1;
    }

    [Benchmark]
    public int IndexedCatalogLookup() => _catalog.GetConsumers(typeof(OutboxBenchmarkEvent)).Count;
}

public sealed record OutboxBenchmarkEvent(int Id, string Name) : IIntegrationEvent;

public sealed record UnrelatedBenchmarkEvent(int Id) : IIntegrationEvent;

internal sealed class CapturingStore : IOutboxStore {
    public OutboxMessage? Last { get; private set; }

    public void Append(OutboxMessage message) => Last = message;

    public ValueTask<IReadOnlyList<OutboxDelivery>> ClaimPendingAsync(
        Guid lockId,
        DateTimeOffset leaseUntil,
        int batchSize,
        IReadOnlyCollection<string> heldRoles,
        CancellationToken ct) => throw new NotSupportedException();

    public ValueTask<bool> MarkProcessedAsync(
        Guid deliveryId,
        Guid lockId,
        DateTimeOffset processedOnUtc,
        CancellationToken ct) => throw new NotSupportedException();

    public ValueTask<bool> MarkFailedAsync(
        Guid deliveryId,
        Guid lockId,
        string error,
        DateTimeOffset retryVisibleAfterUtc,
        CancellationToken ct) => throw new NotSupportedException();

    public ValueTask<bool> MarkPermanentlyFailedAsync(
        Guid deliveryId,
        Guid lockId,
        string error,
        CancellationToken ct) => throw new NotSupportedException();

    public ValueTask<int> PurgeProcessedAsync(DateTimeOffset olderThanUtc, CancellationToken ct) =>
        throw new NotSupportedException();
}

internal sealed class EmptyProvider : IServiceProvider {
    public static readonly EmptyProvider Instance = new();

    public object? GetService(Type serviceType) => null;
}
