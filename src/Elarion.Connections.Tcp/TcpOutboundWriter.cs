using System.Buffers;
using System.Runtime.CompilerServices;
using Elarion.Abstractions.Connections;
using Elarion.Connections.Tcp.Diagnostics;

namespace Elarion.Connections.Tcp;

/// <summary>
/// The connection's bounded FIFO outbound pipeline: admission is count-bounded
/// (<see cref="ElarionTcpConnectionOptions.MaxPendingSends"/>, in-progress work included) and fails
/// deterministically with <see cref="TcpSendQueueFullException"/> at capacity; one writer at a time frames
/// into a pooled, budget-capped buffer and performs the physical stream write, so a send's completion means
/// its complete frame reached the stream — never merely "queued".
/// </summary>
/// <remarks>
/// The uncontended path stays inline and allocation-free: the sender that finds no writer active becomes
/// the writer for its own frame (the cost profile of the old serialized send lock), and only contended
/// senders queue behind it — the finishing writer hands the queue to a drainer task that coalesces queued
/// frames into one physical write per batch. Cancellation before a queued frame is activated withdraws it
/// and emits nothing; a frame the drainer has activated always completes (the batch write is
/// connection-owned); cancellation or failure during the inline physical write aborts the connection,
/// because a partial frame may have corrupted stream boundaries. Closing settles every admitted send
/// exactly once: a graceful close drains, an abort faults active and queued sends alike.
/// </remarks>
internal sealed class TcpOutboundWriter : IDisposable {
    // The drainer flushes a coalesced batch once it holds this many framed bytes — large enough to
    // amortize syscalls and TLS records under contention, small enough to bound pinned pool memory.
    private const int FlushThresholdBytes = 64 * 1024;

    private readonly Lock _gate = new();
    private readonly Queue<PendingSend> _queue = new();
    private readonly List<PendingSend> _drainBatch = [];
    private readonly Stream _stream;
    private readonly TcpMessageFramer _framer;
    private readonly BoundedArrayBufferWriter _frameBuffer;
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TcpConnectionLifetime _lifetime;
    private readonly string _connectionId;
    private readonly string _transport;
    private readonly int _maxPendingSends;
    private readonly int _flushThresholdBytes;
    private readonly int _initialBufferBytes;
    private readonly int _maxFrameBytes;
    private int _pending;
    private bool _writing;
    private bool _closed;
    private Exception? _failure;

    public TcpOutboundWriter(
        Stream stream, TcpMessageFramer framer, int initialBufferBytes, int maxFrameBytes,
        int maxPendingSends, string connectionId, string transport, TcpConnectionLifetime lifetime) {
        _stream = stream;
        _framer = framer;
        _flushThresholdBytes = Math.Min(FlushThresholdBytes, maxFrameBytes);
        _frameBuffer = new BoundedArrayBufferWriter(
            initialBufferBytes, maxFrameBytes, maxFrameBytes + _flushThresholdBytes);
        _maxPendingSends = maxPendingSends;
        _initialBufferBytes = initialBufferBytes;
        _maxFrameBytes = maxFrameBytes;
        _connectionId = connectionId;
        _transport = transport;
        _lifetime = lifetime;
    }

    /// <summary>Resolves once the writer is closed and every admitted send has settled.</summary>
    public Task DrainCompletion => _drained.Task;

    /// <summary>Admits, frames, and physically writes one message; completes after the stream write.</summary>
    /// <remarks>Pooled state machine: the uncontended send is the connection hot path, and the pooled
    /// builder keeps its suspension allocation-free (the same mechanism the BCL socket internals use).</remarks>
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
    public async ValueTask SendAsync(ReadOnlyMemory<byte> message, CancellationToken ct) {
        ct.ThrowIfCancellationRequested();
        PendingSend? queued = null;
        lock (_gate) {
            if (_closed) throw ClosedFault();

            if (_pending >= _maxPendingSends) {
                TcpConnectionTelemetry.RecordOutboundSaturated(_transport);
                throw new TcpSendQueueFullException(_connectionId, _maxPendingSends);
            }

            _pending++;
            if (_writing) {
                queued = new PendingSend(this, message, ct);
                _queue.Enqueue(queued);
            }
            else {
                _writing = true;
            }
        }

        TcpConnectionTelemetry.RecordOutboundAdmitted(_transport);
        if (queued is null) {
            // The inline owner: this sender owns the stream for its own frame — flat in this method so
            // the uncontended path suspends at most one pooled frame — then hands any queue to a drainer.
            // Framing runs through the single Begin/Complete emit path (the static copy-lambda is cached,
            // so the memory-send stays allocation-free).
            var (error, fatal) = await WriteCallerFrameAsync(
                message, static (payload, output) => output.Write(payload.Span), ct);
            FinishWriter(error, fatal);
            if (error is not null) {
                if (fatal) _lifetime.Abort(error);

                throw error;
            }

            return;
        }

        await AwaitQueuedAsync(queued, ct);
    }

    /// <summary>
    /// The writer-based send (ADR-0066): admits one message, lets <paramref name="serialize"/> write the
    /// payload directly into the framed outbound buffer, and completes after the physical stream write —
    /// identical admission, backpressure, and completion semantics to the memory-based
    /// <see cref="SendAsync(ReadOnlyMemory{byte}, CancellationToken)"/>, without the caller materializing a
    /// payload buffer.
    /// </summary>
    /// <remarks>
    /// The callback runs synchronously exactly once. On the uncontended path it serializes straight into the
    /// connection's pooled frame buffer between the framer's <c>BeginMessage</c>/<c>CompleteMessage</c>; a
    /// contended send serializes into a rented per-send buffer and queues with unchanged FIFO/withdrawal
    /// semantics. A serialization or framing failure (including the per-frame budget) faults only this send;
    /// the connection lives.
    /// </remarks>
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
    public async ValueTask SendAsync<TState>(
        TState state, Action<TState, IBufferWriter<byte>> serialize, CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(serialize);
        ct.ThrowIfCancellationRequested();
        var inline = false;
        lock (_gate) {
            if (_closed) throw ClosedFault();

            if (_pending >= _maxPendingSends) {
                TcpConnectionTelemetry.RecordOutboundSaturated(_transport);
                throw new TcpSendQueueFullException(_connectionId, _maxPendingSends);
            }

            _pending++;
            if (!_writing) {
                _writing = true;
                inline = true;
            }
        }

        TcpConnectionTelemetry.RecordOutboundAdmitted(_transport);
        if (inline) {
            var (error, fatal) = await WriteCallerFrameAsync(state, serialize, ct);
            FinishWriter(error, fatal);
            if (error is not null) {
                if (fatal) _lifetime.Abort(error);

                throw error;
            }

            return;
        }

        // Contended: the inline writer owns the shared frame buffer, so this send serializes into its own
        // rented buffer and queues it. Serialization happens outside the gate (arbitrary caller code must
        // never run under the connection lock); the admitted slot is already held, so a failure below
        // releases it deterministically.
        var owned = new BoundedArrayBufferWriter(
            Math.Min(_initialBufferBytes, _maxFrameBytes), _maxFrameBytes);
        try {
            serialize(state, owned);
        }
        catch {
            owned.Dispose();
            ReleaseAdmittedSlot();
            throw;
        }

        PendingSend queued;
        var startDrainer = false;
        lock (_gate) {
            if (_closed) {
                // Closed while serializing: the slot was admitted before the close, so it settles like a
                // faulted queued send rather than draining.
                var fault = ClosedFault();
                owned.Dispose();
                ReleaseAdmittedSlotLocked();
                throw fault;
            }

            queued = new PendingSend(this, owned.WrittenMemory, ct, owned);
            _queue.Enqueue(queued);
            // Serialization ran outside the gate, so the inline writer that made this send contended may
            // have finished meanwhile, found an empty queue, and released stream ownership — enqueueing
            // into an ownerless queue would wait forever. Claiming ownership here mirrors the admission
            // decision the memory-send makes atomically; the drainer settles this frame like any other.
            if (!_writing) {
                _writing = true;
                startDrainer = true;
            }
        }

        if (startDrainer) _ = Task.Run(DrainQueueAsync);

        await AwaitQueuedAsync(queued, ct);
    }

    private void ReleaseAdmittedSlot() {
        lock (_gate) {
            ReleaseAdmittedSlotLocked();
        }

        TcpConnectionTelemetry.RecordOutboundSettled(_transport);
    }

    private void ReleaseAdmittedSlotLocked() {
        _pending--;
        CheckDrainedLocked();
    }

    /// <summary>
    /// Frames one caller-serialized message in place and physically writes it — the writer-send mirror of
    /// <see cref="WriteFrameAsync"/>, with the same fault taxonomy: a serialization/framing failure never
    /// started the frame (non-fatal), a cancelled or failed physical write may have emitted a partial frame
    /// (fatal).
    /// </summary>
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<(Exception? Error, bool Fatal)> WriteCallerFrameAsync<TState>(
        TState state, Action<TState, IBufferWriter<byte>> serialize, CancellationToken ct) {
        try {
            _frameBuffer.ResetWrittenCount();
            var prologueLength = _framer.BeginMessage(_frameBuffer);
            var payloadStart = _frameBuffer.WrittenCount;
            serialize(state, _frameBuffer);
            var payloadLength = _frameBuffer.WrittenCount - payloadStart;
            _framer.CompleteMessage(
                _frameBuffer.GetWrittenSpan(payloadStart - prologueLength, prologueLength),
                _frameBuffer.GetWrittenSpan(payloadStart, payloadLength),
                _frameBuffer);
        }
        catch (Exception framingFailure) {
            _frameBuffer.Trim();
            return (framingFailure, Fatal: false);
        }

        try {
            await _stream.WriteAsync(_frameBuffer.WrittenMemory, ct);
            return (null, false);
        }
        catch (OperationCanceledException cancelled) {
            return (cancelled, Fatal: true);
        }
        catch (Exception ioFailure) {
            return (new ClientConnectionClosedException(_connectionId, ioFailure), Fatal: true);
        }
        finally {
            _frameBuffer.Trim();
        }
    }

    // Separate method so its withdrawal closure cannot hoist a display-class allocation into SendAsync —
    // the uncontended inline path above must stay allocation-free.
    private static async ValueTask AwaitQueuedAsync(PendingSend queued, CancellationToken ct) {
        // Cancellation before the drainer picks the frame up withdraws it — nothing is emitted, the slot
        // frees, and the queue keeps FIFO order for the frames that remain.
        await using var withdrawal = ct.Register(PendingSend.WithdrawCallback, queued);
        await queued.Completion.Task;
    }

    /// <summary>Stops admissions; already-admitted sends keep draining toward <see cref="DrainCompletion"/>.</summary>
    public void BeginGracefulClose() {
        lock (_gate) {
            _closed = true;
            CheckDrainedLocked();
        }
    }

    /// <summary>Stops admissions and faults every queued send. The in-flight physical write, if any, is
    /// faulted by the raw transport disposal that accompanies an abort.</summary>
    public void Abort(Exception? reason) {
        List<PendingSend> toFault;
        Exception fault;
        lock (_gate) {
            _closed = true;
            _failure ??= reason;
            fault = ClosedFault();
            toFault = TakeQueuedLocked();
            CheckDrainedLocked();
        }

        foreach (var send in toFault) {
            send.Completion.TrySetException(fault);
            send.ReleaseOwnedBuffer();
        }
    }

    public void Dispose() {
        _frameBuffer.Dispose();
    }

    // The drainer coalesces every activated frame up to the flush threshold into ONE physical write:
    // fewer syscalls under contention, and over TLS one record per batch instead of one per frame. FIFO
    // order and "completion = my frame physically written" are unchanged; the combined write is
    // connection-owned (no per-item token), so a frame the drainer has activated always completes — a
    // sender's cancellation ends at activation, exactly like bytes already on the wire.
    private async Task DrainQueueAsync() {
        while (true) {
            _drainBatch.Clear();
            _frameBuffer.ResetWrittenCount();
            while (_frameBuffer.WrittenCount < _flushThresholdBytes) {
                PendingSend? item = null;
                lock (_gate) {
                    while (_queue.TryDequeue(out var next))
                        if (next.TryActivate()) {
                            item = next;
                            break;
                        }

                    if (item is null && _drainBatch.Count == 0) {
                        // Emptiness and releasing the stream are decided under one gate, so a sender
                        // admitted concurrently either sees _writing and queues, or becomes the writer.
                        _writing = false;
                        CheckDrainedLocked();
                        return;
                    }
                }

                if (item is null) break;

                _frameBuffer.BeginFrame();
                try {
                    // The same Begin/Complete emit path as every other send route — frame-relative
                    // offsets, because the batch buffer coalesces frames.
                    var prologueLength = _framer.BeginMessage(_frameBuffer);
                    var payloadStart = _frameBuffer.WrittenCount;
                    _frameBuffer.Write(item.Message.Span);
                    _framer.CompleteMessage(
                        _frameBuffer.GetWrittenSpan(payloadStart - prologueLength, prologueLength),
                        _frameBuffer.GetWrittenSpan(payloadStart, item.Message.Length),
                        _frameBuffer);
                    _drainBatch.Add(item);
                }
                catch (Exception framingFailure) {
                    // Oversize/framing failure: the failed frame rewinds out of the buffer; the batch,
                    // the queue, and the connection live on.
                    _frameBuffer.RewindFrame();
                    SettleFramingFailure(item, framingFailure);
                }
            }

            if (_drainBatch.Count == 0) continue;

            Exception? error = null;
            try {
                await _stream.WriteAsync(_frameBuffer.WrittenMemory, CancellationToken.None);
            }
            catch (Exception ioFailure) {
                error = new ClientConnectionClosedException(_connectionId, ioFailure);
            }
            finally {
                _frameBuffer.Trim();
            }

            foreach (var item in _drainBatch) {
                if (error is null)
                    item.Completion.TrySetResult();
                else
                    item.Completion.TrySetException(error);

                // The frame was copied into the batch buffer at activation; a contended writer-send's rented
                // buffer is done once its completion settles.
                item.ReleaseOwnedBuffer();
            }

            List<PendingSend>? toFault = null;
            Exception? fault = null;
            lock (_gate) {
                _pending -= _drainBatch.Count;
                if (error is not null) {
                    _closed = true;
                    _failure ??= error;
                    fault = ClosedFault();
                    toFault = TakeQueuedLocked();
                    _writing = false;
                }

                CheckDrainedLocked();
            }

            for (var i = 0; i < _drainBatch.Count; i++) TcpConnectionTelemetry.RecordOutboundSettled(_transport);

            if (error is not null) {
                foreach (var send in toFault!) {
                    send.Completion.TrySetException(fault!);
                    send.ReleaseOwnedBuffer();
                }

                _lifetime.Abort(error);
                return;
            }
        }
    }

    private void SettleFramingFailure(PendingSend send, Exception failure) {
        send.Completion.TrySetException(failure);
        send.ReleaseOwnedBuffer();
        lock (_gate) {
            _pending--;
            CheckDrainedLocked();
        }

        TcpConnectionTelemetry.RecordOutboundSettled(_transport);
    }

    // Settles the finished inline writer's slot and decides what happens to the stream ownership: hand
    // the queue to a drainer, release it, or — on a fatal write — close and fault everything still queued.
    private void FinishWriter(Exception? error, bool fatal) {
        List<PendingSend>? toFault = null;
        Exception? fault = null;
        var startDrainer = false;
        lock (_gate) {
            _pending--;
            if (fatal) {
                _closed = true;
                _failure ??= error;
                fault = ClosedFault();
                toFault = TakeQueuedLocked();
                _writing = false;
            }
            else if (_queue.Count > 0) {
                startDrainer = true;
            }
            else {
                _writing = false;
            }

            CheckDrainedLocked();
        }

        TcpConnectionTelemetry.RecordOutboundSettled(_transport);
        if (toFault is not null)
            foreach (var send in toFault) {
                send.Completion.TrySetException(fault!);
                send.ReleaseOwnedBuffer();
            }

        if (startDrainer)
            // The finishing sender must not be conscripted into writing other callers' frames — the queue
            // moves to a task of its own. The drainer settles every item it takes, so nothing is unobserved.
            _ = Task.Run(DrainQueueAsync);
    }

    private void Withdraw(PendingSend send) {
        if (!send.TryWithdraw()) return;

        lock (_gate) {
            _pending--;
            CheckDrainedLocked();
        }

        TcpConnectionTelemetry.RecordOutboundSettled(_transport);
        send.Completion.TrySetCanceled(send.Ct);
        send.ReleaseOwnedBuffer();
    }

    // Dequeues everything still pending for faulting; withdrawn items are already settled and skipped.
    private List<PendingSend> TakeQueuedLocked() {
        List<PendingSend> taken = [];
        while (_queue.TryDequeue(out var item))
            if (item.TryActivate()) {
                _pending--;
                TcpConnectionTelemetry.RecordOutboundSettled(_transport);
                taken.Add(item);
            }

        return taken;
    }

    private void CheckDrainedLocked() {
        if (_closed && _pending == 0 && !_writing) _drained.TrySetResult();
    }

    private Exception ClosedFault() {
        return _failure is null || _failure is ClientConnectionClosedException
            ? _failure ?? new ClientConnectionClosedException(_connectionId)
            : new ClientConnectionClosedException(_connectionId, _failure);
    }

    private sealed class PendingSend(
        TcpOutboundWriter owner, ReadOnlyMemory<byte> message, CancellationToken ct,
        BoundedArrayBufferWriter? ownedBuffer = null) {
        /// <summary>Static + state-carrying so registering the withdrawal never allocates a closure.</summary>
        public static readonly Action<object?> WithdrawCallback =
            static state => {
                var send = (PendingSend)state!;
                send.Owner.Withdraw(send);
            };

        private int _state;
        private BoundedArrayBufferWriter? _ownedBuffer = ownedBuffer;

        public TcpOutboundWriter Owner { get; } = owner;
        public ReadOnlyMemory<byte> Message { get; } = message;
        public CancellationToken Ct { get; } = ct;
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Claims the frame for writing (or faulting); loses to an earlier withdrawal.</summary>
        public bool TryActivate() {
            return Interlocked.CompareExchange(ref _state, 1, 0) == 0;
        }

        /// <summary>Claims the frame for cancellation-before-write; loses once a writer activated it.</summary>
        public bool TryWithdraw() {
            return Interlocked.CompareExchange(ref _state, 2, 0) == 0;
        }

        /// <summary>
        /// Returns the rented per-send buffer of a contended writer-send (a no-op for memory-sends). Called
        /// once per settle site after <see cref="Completion"/> settles — <see cref="Message"/> is dead from
        /// then on; idempotent so racing settle paths cannot double-return the array.
        /// </summary>
        public void ReleaseOwnedBuffer() {
            Interlocked.Exchange(ref _ownedBuffer, null)?.Dispose();
        }
    }
}
