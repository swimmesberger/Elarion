using AwesomeAssertions;
using Elarion.Blobs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Elarion.Tests.Blobs;

/// <summary>
/// Fast tests for <see cref="StagedUploadGarbageCollector"/>: it applies the configured
/// <see cref="StagedUploadGcOptions.SafetyMargin"/> to the sweep cutoff, mirroring the blob collector.
/// </summary>
public sealed class StagedUploadGarbageCollectorTests {
    [Fact]
    public async Task Sweep_AppliesSafetyMarginToCutoff() {
        var origin = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var time = new FakeTimeProvider(origin);
        var store = new CapturingStagedUploadStore();
        var options = new StagedUploadGcOptions {
            PollingInterval = TimeSpan.FromMinutes(5),
            SafetyMargin = TimeSpan.FromMinutes(3)
        };
        var services = new ServiceCollection();
        services.AddScoped<IStagedUploadStore>(_ => store);
        using var provider = services.BuildServiceProvider();

        var collector = new StagedUploadGarbageCollector(
            provider.GetRequiredService<IServiceScopeFactory>(),
            options,
            time,
            NullLogger<StagedUploadGarbageCollector>.Instance);

        using var cts = new CancellationTokenSource();
        await collector.StartAsync(cts.Token);
        await store.FirstSweep.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await cts.CancelAsync();
        await collector.StopAsync(CancellationToken.None);

        store.LastOlderThan.Should().Be(origin - TimeSpan.FromMinutes(3));
    }

    private sealed class CapturingStagedUploadStore : IStagedUploadStore {
        public TaskCompletionSource FirstSweep { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public DateTimeOffset LastOlderThan { get; private set; }

        public Task<StagedUpload> CreateAsync(StagedUploadCreation creation, CancellationToken cancellationToken) {
            throw new NotSupportedException();
        }

        public Task<StagedUpload?> GetAsync(string uploadId, CancellationToken cancellationToken) {
            throw new NotSupportedException();
        }

        public Task<StagedUpload> AppendAsync(string uploadId, long offset, Stream chunk,
            CancellationToken cancellationToken) {
            throw new NotSupportedException();
        }

        public Task<StagedUpload> CompleteAsync(string uploadId, StagedUploadCompletion completion,
            CancellationToken cancellationToken) {
            throw new NotSupportedException();
        }

        public Task DeleteAsync(string uploadId, CancellationToken cancellationToken) {
            throw new NotSupportedException();
        }

        public Task<int> DeleteExpiredAsync(DateTimeOffset olderThanUtc, int batchSize,
            CancellationToken cancellationToken) {
            LastOlderThan = olderThanUtc;
            FirstSweep.TrySetResult();
            return Task.FromResult(0);
        }
    }
}
