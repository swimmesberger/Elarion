using AwesomeAssertions;
using Elarion.Blobs;
using Elarion.Blobs.Tus;
using Elarion.Blobs.Tus.PostgreSql;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Elarion.Tests.Blobs;

/// <summary>
/// Fast tests for <see cref="TusUploadGarbageCollector"/>: it applies the configured
/// <see cref="TusGcOptions.SafetyMargin"/> to the sweep cutoff, mirroring the blob collector (TUS low a).
/// </summary>
public sealed class TusUploadGarbageCollectorTests {
    [Fact]
    public async Task Sweep_AppliesSafetyMarginToCutoff() {
        var origin = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var time = new FakeTimeProvider(origin);
        var store = new CapturingTusUploadStore();
        var options = new TusGcOptions {
            PollingInterval = TimeSpan.FromMinutes(5),
            SafetyMargin = TimeSpan.FromMinutes(3)
        };
        var services = new ServiceCollection();
        services.AddScoped<ITusUploadStore>(_ => store);
        using var provider = services.BuildServiceProvider();

        var collector = new TusUploadGarbageCollector(
            provider.GetRequiredService<IServiceScopeFactory>(),
            options,
            time,
            NullLogger<TusUploadGarbageCollector>.Instance);

        using var cts = new CancellationTokenSource();
        await collector.StartAsync(cts.Token);
        await store.FirstSweep.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await cts.CancelAsync();
        await collector.StopAsync(CancellationToken.None);

        store.LastOlderThan.Should().Be(origin - TimeSpan.FromMinutes(3));
    }

    private sealed class CapturingTusUploadStore : ITusUploadStore {
        public TaskCompletionSource FirstSweep { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public DateTimeOffset LastOlderThan { get; private set; }

        public Task<TusUpload> CreateAsync(TusUploadCreation creation, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<TusUpload?> GetAsync(string uploadId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<TusUpload> AppendAsync(string uploadId, long offset, Stream chunk, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task DeleteAsync(string uploadId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<int> DeleteExpiredAsync(DateTimeOffset olderThanUtc, int batchSize, CancellationToken cancellationToken) {
            LastOlderThan = olderThanUtc;
            FirstSweep.TrySetResult();
            return Task.FromResult(0);
        }
    }
}
