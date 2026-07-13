using System.Collections.Concurrent;
using System.Threading.Channels;
using AwesomeAssertions;
using Elarion.Buffering;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Elarion.Tests.Buffering;

/// <summary>
/// The ADR-0055 write-behind contract: flush on MaxItems or FlushInterval (whichever first), bounded
/// drop-oldest, single-flight flushes, explicit FlushAsync, and flush-on-dispose.
/// </summary>
public sealed class WriteBehindBufferTests {
    private static CancellationToken TestToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ReachingMaxItems_FlushesTheBatch_InOrder() {
        var batches = Channel.CreateUnbounded<IReadOnlyList<int>>();
        await using var buffer = new WriteBehindBuffer<int>(
            async (batch, _) => await batches.Writer.WriteAsync(batch, TestToken),
            new WriteBehindBufferOptions { MaxItems = 3, FlushInterval = Timeout.InfiniteTimeSpan });

        buffer.Add(1);
        buffer.Add(2);
        buffer.Add(3);

        var flushed = await batches.Reader.ReadAsync(TestToken);
        flushed.Should().Equal(1, 2, 3);
        buffer.Count.Should().Be(0);
    }

    [Fact]
    public async Task BelowMaxItems_TheIntervalFlushes_WhatAccumulated() {
        var time = new FakeTimeProvider();
        var batches = Channel.CreateUnbounded<IReadOnlyList<int>>();
        await using var buffer = new WriteBehindBuffer<int>(
            async (batch, _) => await batches.Writer.WriteAsync(batch, TestToken),
            new WriteBehindBufferOptions {
                MaxItems = 100, FlushInterval = TimeSpan.FromSeconds(10), TimeProvider = time,
            });

        buffer.Add(1);
        buffer.Add(2);

        time.Advance(TimeSpan.FromSeconds(9));
        batches.Reader.TryRead(out _).Should().BeFalse();

        time.Advance(TimeSpan.FromSeconds(1));
        var flushed = await batches.Reader.ReadAsync(TestToken);
        flushed.Should().Equal(1, 2);
    }

    [Fact]
    public async Task AnInfiniteInterval_NeverFlushesByTime() {
        var flushed = 0;
        await using var buffer = new WriteBehindBuffer<int>(
            (_, _) => {
                Interlocked.Increment(ref flushed);
                return ValueTask.CompletedTask;
            },
            new WriteBehindBufferOptions {
                MaxItems = 100, FlushInterval = Timeout.InfiniteTimeSpan, TimeProvider = new FakeTimeProvider(),
            });

        buffer.Add(1);
        buffer.Count.Should().Be(1);
        flushed.Should().Be(0);
    }

    [Fact]
    public async Task BeyondCapacity_DropsOldest_AndCountsThem() {
        var firstBatchStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstBatch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var batches = new ConcurrentQueue<IReadOnlyList<int>>();

        await using var buffer = new WriteBehindBuffer<int>(
            async (batch, _) => {
                batches.Enqueue(batch);
                if (batches.Count == 1) {
                    firstBatchStarted.TrySetResult();
                    await releaseFirstBatch.Task;
                }
            },
            new WriteBehindBufferOptions { MaxItems = 2, Capacity = 4, FlushInterval = Timeout.InfiniteTimeSpan });

        buffer.Add(1);
        buffer.Add(2);
        await firstBatchStarted.Task.WaitAsync(TestToken);

        // The flush target is stalled: five more samples against capacity 4 evict the oldest one.
        for (var i = 3; i <= 7; i++) {
            buffer.Add(i);
        }

        buffer.Count.Should().Be(4);
        buffer.DroppedCount.Should().Be(1);
        releaseFirstBatch.SetResult();

        await buffer.FlushAsync(TestToken);
        batches.SelectMany(b => b).Should().Equal(1, 2, 4, 5, 6, 7); // 3 was evicted oldest-first
    }

    [Fact]
    public async Task Flushes_AreSingleFlight_AndCoalesceWorkAddedMeanwhile() {
        var firstBatchStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstBatch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var batches = new ConcurrentQueue<IReadOnlyList<int>>();
        var concurrent = 0;
        var maxConcurrent = 0;

        await using var buffer = new WriteBehindBuffer<int>(
            async (batch, _) => {
                var now = Interlocked.Increment(ref concurrent);
                InterlockedMax(ref maxConcurrent, now);
                batches.Enqueue(batch);
                if (batches.Count == 1) {
                    firstBatchStarted.TrySetResult();
                    await releaseFirstBatch.Task;
                }

                Interlocked.Decrement(ref concurrent);
            },
            new WriteBehindBufferOptions { MaxItems = 2, FlushInterval = Timeout.InfiniteTimeSpan });

        buffer.Add(1);
        buffer.Add(2);
        await firstBatchStarted.Task.WaitAsync(TestToken);

        // The first flush is blocked inside the delegate; these coalesce into the next drain pass.
        buffer.Add(3);
        buffer.Add(4);
        buffer.Add(5);
        releaseFirstBatch.SetResult();

        await buffer.FlushAsync(TestToken);
        batches.SelectMany(b => b).Should().Equal(1, 2, 3, 4, 5);
        maxConcurrent.Should().Be(1);
    }

    [Fact]
    public async Task ExplicitFlush_PropagatesTheDelegateFailure_AndDropsTheBatch() {
        var calls = 0;
        var buffer = new WriteBehindBuffer<int>(
            (_, _) => calls++ == 0
                ? ValueTask.FromException(new InvalidOperationException("sink down"))
                : ValueTask.CompletedTask,
            new WriteBehindBufferOptions { MaxItems = 100, FlushInterval = Timeout.InfiniteTimeSpan });

        buffer.Add(1);
        var act = async () => await buffer.FlushAsync(TestToken);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("sink down");

        // The failed batch is dropped (loss-tolerant), and the buffer keeps working.
        buffer.Count.Should().Be(0);
        buffer.Add(2);
        await buffer.FlushAsync(TestToken);
        calls.Should().Be(2);
        await buffer.DisposeAsync();
    }

    [Fact]
    public async Task BackgroundFlushFailures_ReachTheErrorCallback_WithTheDroppedBatch() {
        var errors = Channel.CreateUnbounded<(Exception Exception, IReadOnlyList<int> Batch)>();
        await using var buffer = new WriteBehindBuffer<int>(
            (_, _) => ValueTask.FromException(new InvalidOperationException("sink down")),
            new WriteBehindBufferOptions { MaxItems = 2, FlushInterval = Timeout.InfiniteTimeSpan },
            (exception, batch) => errors.Writer.TryWrite((exception, batch)));

        buffer.Add(1);
        buffer.Add(2);

        var (error, dropped) = await errors.Reader.ReadAsync(TestToken);
        error.Should().BeOfType<InvalidOperationException>();
        dropped.Should().Equal(1, 2);
        buffer.Count.Should().Be(0);
    }

    [Fact]
    public async Task Dispose_FlushesTheTail_ThenDropsLateAdds() {
        var batches = new ConcurrentQueue<IReadOnlyList<int>>();
        var buffer = new WriteBehindBuffer<int>(
            (batch, _) => {
                batches.Enqueue(batch);
                return ValueTask.CompletedTask;
            },
            new WriteBehindBufferOptions { MaxItems = 100, FlushInterval = Timeout.InfiniteTimeSpan });

        buffer.Add(1);
        buffer.Add(2);
        await buffer.DisposeAsync();

        batches.Should().ContainSingle().Which.Should().Equal(1, 2);

        buffer.Add(3);
        buffer.Count.Should().Be(0);
        var act = async () => await buffer.FlushAsync(TestToken);
        await act.Should().ThrowAsync<ObjectDisposedException>();
    }

    [Fact]
    public async Task Dispose_RoutesAFailedFinalFlush_ToTheErrorCallback_InsteadOfThrowing() {
        Exception? observed = null;
        var buffer = new WriteBehindBuffer<int>(
            (_, _) => ValueTask.FromException(new InvalidOperationException("sink down")),
            new WriteBehindBufferOptions { MaxItems = 100, FlushInterval = Timeout.InfiniteTimeSpan },
            (exception, _) => observed = exception);

        buffer.Add(1);
        await buffer.DisposeAsync();

        observed.Should().BeOfType<InvalidOperationException>();
    }

    [Fact]
    public async Task FlushAsync_OnAnEmptyBuffer_NeverCallsTheDelegate() {
        var calls = 0;
        await using var buffer = new WriteBehindBuffer<int>(
            (_, _) => {
                calls++;
                return ValueTask.CompletedTask;
            },
            new WriteBehindBufferOptions { FlushInterval = Timeout.InfiniteTimeSpan });

        await buffer.FlushAsync(TestToken);
        calls.Should().Be(0);
    }

    [Fact]
    public async Task Options_AreValidated() {
        Func<IReadOnlyList<int>, CancellationToken, ValueTask> flush = (_, _) => ValueTask.CompletedTask;

        var zeroMaxItems = () => new WriteBehindBuffer<int>(flush, new WriteBehindBufferOptions { MaxItems = 0 });
        zeroMaxItems.Should().Throw<ArgumentOutOfRangeException>();

        var zeroInterval = () => new WriteBehindBuffer<int>(
            flush, new WriteBehindBufferOptions { FlushInterval = TimeSpan.Zero });
        zeroInterval.Should().Throw<ArgumentOutOfRangeException>();

        var tinyCapacity = () => new WriteBehindBuffer<int>(
            flush, new WriteBehindBufferOptions { MaxItems = 10, Capacity = 5 });
        tinyCapacity.Should().Throw<ArgumentOutOfRangeException>();

        await using var defaults = new WriteBehindBuffer<int>(flush);
        defaults.Count.Should().Be(0);
    }

    private static void InterlockedMax(ref int target, int value) {
        int current;
        while ((current = Volatile.Read(ref target)) < value &&
               Interlocked.CompareExchange(ref target, value, current) != current) {
        }
    }
}
