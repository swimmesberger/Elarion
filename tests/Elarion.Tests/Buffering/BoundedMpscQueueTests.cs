using System.Runtime.CompilerServices;
using AwesomeAssertions;
using Elarion.Buffering;
using Xunit;

namespace Elarion.Tests.Buffering;

/// <summary>
/// The ADR-0069 bounded MPSC queue contract: FIFO per producer, full → <c>false</c> (backpressure is
/// the caller's), sequence-based wraparound, exactly-once delivery under producer contention, zero
/// allocation per operation, and reference hygiene on dequeued slots.
/// </summary>
public sealed class BoundedMpscQueueTests {
    [Fact]
    public void SingleProducer_DequeuesInFifoOrder() {
        var queue = new BoundedMpscQueue<int>(8);
        for (var i = 0; i < 5; i++)
            queue.TryEnqueue(i).Should().BeTrue();

        for (var i = 0; i < 5; i++) {
            queue.TryDequeue(out var item).Should().BeTrue();
            item.Should().Be(i);
        }

        queue.TryDequeue(out _).Should().BeFalse();
    }

    [Fact]
    public void Capacity_RoundsUpToThePowerOfTwo() {
        new BoundedMpscQueue<int>(1).Capacity.Should().Be(1);
        new BoundedMpscQueue<int>(3).Capacity.Should().Be(4);
        new BoundedMpscQueue<int>(1000).Capacity.Should().Be(1024);
    }

    [Fact]
    public void AFullQueue_RejectsTheEnqueue_AndRecoversAfterOneDequeue() {
        var queue = new BoundedMpscQueue<int>(4);
        for (var i = 0; i < 4; i++)
            queue.TryEnqueue(i).Should().BeTrue();

        queue.TryEnqueue(99).Should().BeFalse();

        queue.TryDequeue(out var oldest).Should().BeTrue();
        oldest.Should().Be(0);
        queue.TryEnqueue(99).Should().BeTrue();
    }

    [Fact]
    public void ManyLaps_WrapAroundBySequence_NotIndexReset() {
        var queue = new BoundedMpscQueue<int>(4);
        // Uneven fill levels walk the cursors across many laps, so slot sequences advance by capacity
        // per lap rather than resetting — the arithmetic the wraparound contract pins.
        var next = 0;
        var expected = 0;
        for (var round = 0; round < 10_000; round++) {
            queue.TryEnqueue(next++).Should().BeTrue();
            queue.TryEnqueue(next++).Should().BeTrue();
            queue.TryEnqueue(next++).Should().BeTrue();

            queue.TryDequeue(out var a).Should().BeTrue();
            queue.TryDequeue(out var b).Should().BeTrue();
            queue.TryDequeue(out var c).Should().BeTrue();
            a.Should().Be(expected++);
            b.Should().Be(expected++);
            c.Should().Be(expected++);
        }
    }

    [Fact]
    public async Task ContendedProducers_DeliverEveryItemExactlyOnce_FifoPerProducer() {
        const int producers = 4;
        const int perProducer = 20_000;
        var queue = new BoundedMpscQueue<long>(256);

        var producerTasks = Enumerable.Range(0, producers).Select(p => Task.Run(() => {
            for (var i = 0; i < perProducer; i++) {
                var item = ((long)p << 32) | (uint)i;
                var spinner = new SpinWait();
                while (!queue.TryEnqueue(item))
                    spinner.SpinOnce(); // full — a must-land producer retries by contract
            }
        })).ToArray();

        var nextPerProducer = new int[producers];
        var received = 0;
        var consumerSpinner = new SpinWait();
        while (received < producers * perProducer) {
            if (!queue.TryDequeue(out var item)) {
                consumerSpinner.SpinOnce();
                continue;
            }

            consumerSpinner.Reset();
            var producer = (int)(item >> 32);
            var sequence = (int)(uint)item;
            sequence.Should().Be(nextPerProducer[producer], "each producer's items must arrive in FIFO order");
            nextPerProducer[producer] = sequence + 1;
            received++;
        }

        await Task.WhenAll(producerTasks);
        nextPerProducer.Should().AllSatisfy(next => next.Should().Be(perProducer));
        queue.TryDequeue(out _).Should().BeFalse();
    }

    [Fact]
    public void HotPathOperations_AllocateNothing() {
        var queue = new BoundedMpscQueue<long>(64);
        for (var i = 0; i < 1_000; i++) {
            queue.TryEnqueue(i).Should().BeTrue();
            queue.TryDequeue(out _).Should().BeTrue();
        }

        const int iterations = 100_000;
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < iterations; i++) {
            queue.TryEnqueue(i);
            queue.TryDequeue(out _);
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        allocated.Should().Be(0, "TryEnqueue and TryDequeue are the family's zero-allocation promise");
    }

    [Fact]
    public void ADequeuedSlot_DoesNotKeepItemReferencesAlive() {
        var queue = new BoundedMpscQueue<PayloadCommand>(4);
        var weak = EnqueueDequeueAndForget(queue);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        weak.IsAlive.Should().BeFalse("the dequeued slot must be cleared, not hold the payload until the slot's next lap");
        GC.KeepAlive(queue);
    }

    // Not inlined so the payload and the dequeued copy never live in the test method's frame,
    // where the JIT could keep them alive past the collection.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference EnqueueDequeueAndForget(BoundedMpscQueue<PayloadCommand> queue) {
        var payload = new byte[16];
        queue.TryEnqueue(new PayloadCommand { Payload = payload }).Should().BeTrue();
        queue.TryDequeue(out _).Should().BeTrue();
        return new WeakReference(payload);
    }

    private struct PayloadCommand {
        public byte[]? Payload;
    }
}
