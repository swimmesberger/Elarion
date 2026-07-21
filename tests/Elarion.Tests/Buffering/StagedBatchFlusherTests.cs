using AwesomeAssertions;
using Elarion.Buffering;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Elarion.Tests.Buffering;

/// <summary>
/// The ADR-0069 staged-batch handoff contract: single-flight ownership transfer (submit → written →
/// idle again), skip-and-retry while busy, error policy that returns ownership and keeps the loop
/// alive, dispose draining, <c>FlushAsync</c> releasing only at idle, and a zero-allocation submit path.
/// </summary>
public sealed class StagedBatchFlusherTests {
    private static CancellationToken TestToken => TestContext.Current.CancellationToken;

    private sealed class Batch {
        public List<int> Rows { get; } = [];
    }

    [Fact]
    public async Task Submit_WritesTheBatch_AndReturnsToIdle() {
        var written = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var flusher = new StagedBatchFlusher<Batch>(
            (batch, _) => {
                written.TrySetResult(batch.Rows.Count);
                return ValueTask.CompletedTask;
            });
        var staged = new Batch();
        staged.Rows.AddRange([1, 2, 3]);

        flusher.IsIdle.Should().BeTrue();
        flusher.TrySubmit(staged).Should().BeTrue();

        (await written.Task.WaitAsync(TestToken)).Should().Be(3);
        await flusher.FlushAsync(TestToken);
        flusher.IsIdle.Should().BeTrue();
    }

    [Fact]
    public async Task WhileABatchIsInFlight_TrySubmitReturnsFalse_AndSucceedsAfterCompletion() {
        var writeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var flusher = new StagedBatchFlusher<Batch>(
            async (_, _) => {
                writeStarted.TrySetResult();
                await releaseWrite.Task;
            });
        var batch = new Batch();

        flusher.TrySubmit(batch).Should().BeTrue();
        await writeStarted.Task.WaitAsync(TestToken);

        flusher.IsIdle.Should().BeFalse();
        flusher.TrySubmit(batch).Should().BeFalse("a batch is in flight — the producer keeps its dirty flags and retries");

        releaseWrite.TrySetResult();
        await flusher.FlushAsync(TestToken);
        flusher.TrySubmit(batch).Should().BeTrue();
    }

    [Fact]
    public async Task AThrowingWriteDelegate_RoutesToOnFlushError_ReturnsOwnership_AndKeepsTheLoopAlive() {
        var failures = new List<(Exception Exception, Batch Batch)>();
        var secondWrite = new TaskCompletionSource<Batch>(TaskCreationOptions.RunContinuationsAsynchronously);
        var shouldThrow = true;
        await using var flusher = new StagedBatchFlusher<Batch>(
            (batch, _) => {
                if (shouldThrow) throw new InvalidOperationException("write target down");

                secondWrite.TrySetResult(batch);
                return ValueTask.CompletedTask;
            },
            onFlushError: (exception, batch) => failures.Add((exception, batch)));
        var staged = new Batch();

        flusher.TrySubmit(staged).Should().BeTrue();
        await flusher.FlushAsync(TestToken);

        flusher.IsIdle.Should().BeTrue("ownership returns to the producer even when the write throws");
        failures.Should().ContainSingle().Which.Exception.Message.Should().Be("write target down");
        failures[0].Batch.Should().BeSameAs(staged);

        shouldThrow = false;
        flusher.TrySubmit(staged).Should().BeTrue("a failed write must not tear down the writer loop");
        (await secondWrite.Task.WaitAsync(TestToken)).Should().BeSameAs(staged);
    }

    [Fact]
    public async Task TheResetCallback_RunsAtOwnershipReturn_BeforeIdleIsObservable() {
        var order = new List<string>();
        await using var flusher = new StagedBatchFlusher<Batch>(
            (_, _) => {
                order.Add("write");
                return ValueTask.CompletedTask;
            },
            reset: batch => {
                order.Add("reset");
                batch.Rows.Clear();
            });
        var staged = new Batch();
        staged.Rows.Add(1);

        flusher.TrySubmit(staged).Should().BeTrue();
        await flusher.FlushAsync(TestToken);

        order.Should().Equal("write", "reset");
        staged.Rows.Should().BeEmpty("the reset convenience ran before the producer could observe idle");
    }

    [Fact]
    public async Task FlushAsync_ReleasesOnlyAtIdle() {
        var writeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var flusher = new StagedBatchFlusher<Batch>(
            async (_, _) => {
                writeStarted.TrySetResult();
                await releaseWrite.Task;
            });

        flusher.TrySubmit(new Batch()).Should().BeTrue();
        await writeStarted.Task.WaitAsync(TestToken);

        var flush = flusher.FlushAsync(TestToken);
        await Task.Delay(50, TestToken);
        flush.IsCompleted.Should().BeFalse("the write is still in flight");

        releaseWrite.TrySetResult();
        await flush.WaitAsync(TestToken);
        flusher.IsIdle.Should().BeTrue();
    }

    [Fact]
    public async Task Dispose_WritesAPendingBatch_BeforeStopping() {
        var written = new TaskCompletionSource<Batch>(TaskCreationOptions.RunContinuationsAsynchronously);
        var flusher = new StagedBatchFlusher<Batch>(
            (batch, _) => {
                written.TrySetResult(batch);
                return ValueTask.CompletedTask;
            });
        var staged = new Batch();

        flusher.TrySubmit(staged).Should().BeTrue();
        await flusher.DisposeAsync();

        written.Task.IsCompletedSuccessfully.Should().BeTrue("a batch submitted before disposal is written");
        (await written.Task).Should().BeSameAs(staged);
        flusher.TrySubmit(staged).Should().BeFalse("submits after dispose are rejected");
    }

    [Fact]
    public async Task ABoundedDisposeTimeout_AbandonsAWedgedWrite() {
        var time = new FakeTimeProvider();
        var writeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var flusher = new StagedBatchFlusher<Batch>(
            async (_, _) => {
                writeStarted.TrySetResult();
                await releaseWrite.Task;
            },
            new StagedBatchFlusherOptions { DisposeTimeout = TimeSpan.FromSeconds(5), TimeProvider = time });

        flusher.TrySubmit(new Batch()).Should().BeTrue();
        await writeStarted.Task.WaitAsync(TestToken);

        var dispose = flusher.DisposeAsync().AsTask();
        time.Advance(TimeSpan.FromSeconds(5));
        await dispose.WaitAsync(TestToken);

        releaseWrite.TrySetResult(); // let the abandoned write finish so nothing leaks into other tests
    }

    [Fact]
    public async Task TheSubmitPath_AllocatesNothing() {
        await using var flusher = new StagedBatchFlusher<Batch>(static (_, _) => ValueTask.CompletedTask);
        var batch = new Batch();

        for (var i = 0; i < 1_000; i++) {
            flusher.TrySubmit(batch);
            var spinner = new SpinWait();
            while (!flusher.IsIdle)
                spinner.SpinOnce();
        }

        const int iterations = 10_000;
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < iterations; i++) {
            flusher.TrySubmit(batch);
            var spinner = new SpinWait();
            while (!flusher.IsIdle)
                spinner.SpinOnce();
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // Zero expected from the flusher itself, but Release can hand the writer's continuation to the
        // thread pool, whose queue bookkeeping (segment/array growth) is occasionally charged to this
        // thread. The slack absorbs those one-offs without letting a real per-submit allocation
        // (≥ 24 B each, ≥ 240 KB total) sneak past.
        var bytesPerOp = allocated / (double)iterations;
        bytesPerOp.Should().BeLessThan(1.0,
            $"TrySubmit and the IsIdle gate are the producer's hot path and must not allocate, but measured {bytesPerOp:F2} B/op");
    }
}
