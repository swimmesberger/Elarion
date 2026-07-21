using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Elarion.Buffering;

/// <summary>
/// A fixed-capacity, array-backed, lock-free queue of structs for many producers and one consumer —
/// the "commands into a single-writer loop" primitive of the ADR-0055 family (ADR-0069). A full queue
/// rejects the enqueue instead of blocking or growing: the <em>caller</em> decides per message class
/// whether full means drop (loss-tolerant telemetry) or retry/fail (must-land control messages).
/// <see cref="TryEnqueue"/> and <see cref="TryDequeue"/> allocate nothing; all slots are preallocated
/// at construction.
/// </summary>
/// <remarks>
/// <para>
/// <b>Single-consumer contract.</b> <see cref="TryEnqueue"/> is safe from any number of threads;
/// <see cref="TryDequeue"/> must never run concurrently with itself — one consumer at a time
/// (migrating the consumer between threads is fine as long as calls do not overlap, e.g. an async tick
/// loop hopping pool threads). Concurrent dequeues are undefined behavior; a debug-only reentrancy
/// assertion catches violations in DEBUG builds.
/// </para>
/// <para>
/// <b>Ordering.</b> FIFO per producer. Cross-producer arrival order is slot-claim order (the order the
/// enqueue cursor was won), which under contention may differ from call order — Vyukov's bounded-queue
/// guarantee, not a global happened-before.
/// </para>
/// <para>
/// <b>Memory layout.</b> The enqueue and dequeue cursors live on separate cache lines so producers and
/// the consumer do not ping-pong one line. Slots are deliberately <em>not</em> padded: commands are
/// small and per-slot padding would multiply the queue's footprint by the cache-line size; adjacent-slot
/// sharing only costs on the brief publish/consume of neighboring slots.
/// </para>
/// </remarks>
/// <typeparam name="T">The command/message struct. It may contain object references — a dequeued slot
/// is cleared so the queue never keeps such references alive until the slot's next reuse.</typeparam>
public sealed class BoundedMpscQueue<T> where T : struct {
    private readonly Cell[] _cells;
    private readonly long _mask;
    private PaddedCursor _enqueueCursor;
    private PaddedCursor _dequeueCursor;
#if DEBUG
    private int _consumerGuard;
#endif

    /// <summary>Creates a queue with at least <paramref name="capacity"/> slots.</summary>
    /// <param name="capacity">The minimum capacity; rounded up to the next power of two (the slot index
    /// is a mask of the cursor). Must be between 1 and 2^30.</param>
    public BoundedMpscQueue(int capacity) {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(capacity, 1 << 30);

        var size = (int)BitOperations.RoundUpToPowerOf2((uint)capacity);
        _cells = new Cell[size];
        _mask = size - 1;
        for (var i = 0; i < size; i++)
            _cells[i].Sequence = i;
    }

    /// <summary>The actual slot count — the requested capacity rounded up to a power of two.</summary>
    public int Capacity => _cells.Length;

    /// <summary>
    /// Enqueues one item from any thread; <see langword="false"/> means the queue is full and the item
    /// was not stored. Never blocks, never allocates — backpressure policy (drop, retry, fail) belongs
    /// to the caller.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryEnqueue(in T item) {
        var cells = _cells;
        var pos = Volatile.Read(ref _enqueueCursor.Value);
        while (true) {
            ref var cell = ref cells[(int)(pos & _mask)];
            var diff = Volatile.Read(ref cell.Sequence) - pos;
            if (diff == 0) {
                if (Interlocked.CompareExchange(ref _enqueueCursor.Value, pos + 1, pos) == pos) {
                    cell.Value = item;
                    Volatile.Write(ref cell.Sequence, pos + 1); // publish: consumer may read from here on
                    return true;
                }
            }
            else if (diff < 0) {
                // The slot one full lap behind the cursor has not been dequeued yet — the queue is full.
                return false;
            }

            pos = Volatile.Read(ref _enqueueCursor.Value);
        }
    }

    /// <summary>
    /// Dequeues one item; <see langword="false"/> means the queue is empty. Single consumer only —
    /// concurrent calls are undefined (see the type remarks). Never blocks, never allocates.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryDequeue(out T item) {
        AssertSingleConsumerEnter();
        var pos = _dequeueCursor.Value; // only the consumer touches this cursor — no fence needed
        ref var cell = ref _cells[(int)(pos & _mask)];
        if (Volatile.Read(ref cell.Sequence) - (pos + 1) != 0) {
            // The producer that claimed this slot has not published yet (or no producer has) — empty.
            item = default;
            AssertSingleConsumerExit();
            return false;
        }

        item = cell.Value;
        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
            cell.Value = default; // don't keep the item's references alive until the slot's next lap
        Volatile.Write(ref cell.Sequence, pos + _cells.Length); // recycle: producers may claim from here on
        _dequeueCursor.Value = pos + 1;
        AssertSingleConsumerExit();
        return true;
    }

    [Conditional("DEBUG")]
    private void AssertSingleConsumerEnter() {
#if DEBUG
        // A reentrancy guard rather than a thread capture: the contract forbids overlapping dequeues,
        // not consumer migration between threads.
        Debug.Assert(
            Interlocked.Exchange(ref _consumerGuard, 1) == 0,
            $"{nameof(BoundedMpscQueue<>)}.{nameof(TryDequeue)} was called concurrently — the queue has a single-consumer contract.");
#endif
    }

    [Conditional("DEBUG")]
    private void AssertSingleConsumerExit() {
#if DEBUG
        Volatile.Write(ref _consumerGuard, 0);
#endif
    }

    private struct Cell {
        /// <summary>Vyukov slot state: <c>pos</c> = free for the enqueuer that claims <c>pos</c>;
        /// <c>pos + 1</c> = published, ready for the consumer; <c>pos + Capacity</c> = free for the next lap.</summary>
        public long Sequence;
        public T Value;
    }
}

/// <summary>A cursor alone on its cache line, so producers (enqueue cursor) and the consumer (dequeue
/// cursor) do not invalidate each other's line on every operation. Not nested in the queue: a nested
/// struct would be generic over <c>T</c>, and generic types cannot have explicit layout.</summary>
[StructLayout(LayoutKind.Explicit, Size = 128)]
internal struct PaddedCursor {
    [FieldOffset(64)] public long Value;
}
