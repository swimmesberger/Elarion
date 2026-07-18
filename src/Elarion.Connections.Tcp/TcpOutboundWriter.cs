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
            if (_closed) {
                throw ClosedFault();
            }

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
            var (error, fatal) = await WriteFrameAsync(message, ct);
            FinishWriter(error, fatal);
            if (error is not null) {
                if (fatal) {
                    _lifetime.Abort(error);
                }

                throw error;
            }

            return;
        }

        await AwaitQueuedAsync(queued, ct);
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
        }
    }

    public void Dispose() => _frameBuffer.Dispose();

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
                    while (_queue.TryDequeue(out var next)) {
                        if (next.TryActivate()) {
                            item = next;
                            break;
                        }
                    }

                    if (item is null && _drainBatch.Count == 0) {
                        // Emptiness and releasing the stream are decided under one gate, so a sender
                        // admitted concurrently either sees _writing and queues, or becomes the writer.
                        _writing = false;
                        CheckDrainedLocked();
                        return;
                    }
                }

                if (item is null) {
                    break;
                }

                _frameBuffer.BeginFrame();
                try {
                    _framer.WriteMessage(item.Message.Span, _frameBuffer);
                    _drainBatch.Add(item);
                }
                catch (Exception framingFailure) {
                    // Oversize/framing failure: the failed frame rewinds out of the buffer; the batch,
                    // the queue, and the connection live on.
                    _frameBuffer.RewindFrame();
                    SettleFramingFailure(item, framingFailure);
                }
            }

            if (_drainBatch.Count == 0) {
                continue;
            }

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
                if (error is null) {
                    item.Completion.TrySetResult();
                }
                else {
                    item.Completion.TrySetException(error);
                }
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

            for (var i = 0; i < _drainBatch.Count; i++) {
                TcpConnectionTelemetry.RecordOutboundSettled(_transport);
            }

            if (error is not null) {
                foreach (var send in toFault!) {
                    send.Completion.TrySetException(fault!);
                }

                _lifetime.Abort(error);
                return;
            }
        }
    }

    private void SettleFramingFailure(PendingSend send, Exception failure) {
        send.Completion.TrySetException(failure);
        lock (_gate) {
            _pending--;
            CheckDrainedLocked();
        }

        TcpConnectionTelemetry.RecordOutboundSettled(_transport);
    }

    /// <summary>
    /// Frames and physically writes one message. Returns the send's fault (or <see langword="null"/> on
    /// success) plus whether it is connection-fatal: a framing/oversize failure never started the frame, so
    /// the connection lives; a cancelled or failed physical write may have left a partial frame, so the
    /// stream can no longer be trusted to carry message boundaries.
    /// </summary>
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<(Exception? Error, bool Fatal)> WriteFrameAsync(
        ReadOnlyMemory<byte> message, CancellationToken ct) {
        try {
            _frameBuffer.ResetWrittenCount();
            _framer.WriteMessage(message.Span, _frameBuffer);
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
        if (toFault is not null) {
            foreach (var send in toFault) {
                send.Completion.TrySetException(fault!);
            }
        }

        if (startDrainer) {
            // The finishing sender must not be conscripted into writing other callers' frames — the queue
            // moves to a task of its own. The drainer settles every item it takes, so nothing is unobserved.
            _ = Task.Run(DrainQueueAsync);
        }
    }

    private void Withdraw(PendingSend send) {
        if (!send.TryWithdraw()) {
            return;
        }

        lock (_gate) {
            _pending--;
            CheckDrainedLocked();
        }

        TcpConnectionTelemetry.RecordOutboundSettled(_transport);
        send.Completion.TrySetCanceled(send.Ct);
    }

    // Dequeues everything still pending for faulting; withdrawn items are already settled and skipped.
    private List<PendingSend> TakeQueuedLocked() {
        List<PendingSend> taken = [];
        while (_queue.TryDequeue(out var item)) {
            if (item.TryActivate()) {
                _pending--;
                TcpConnectionTelemetry.RecordOutboundSettled(_transport);
                taken.Add(item);
            }
        }

        return taken;
    }

    private void CheckDrainedLocked() {
        if (_closed && _pending == 0 && !_writing) {
            _drained.TrySetResult();
        }
    }

    private Exception ClosedFault() =>
        _failure is null || _failure is ClientConnectionClosedException
            ? _failure ?? new ClientConnectionClosedException(_connectionId)
            : new ClientConnectionClosedException(_connectionId, _failure);

    private sealed class PendingSend(TcpOutboundWriter owner, ReadOnlyMemory<byte> message, CancellationToken ct) {
        /// <summary>Static + state-carrying so registering the withdrawal never allocates a closure.</summary>
        public static readonly Action<object?> WithdrawCallback =
            static state => { var send = (PendingSend)state!; send.Owner.Withdraw(send); };

        private int _state;

        public TcpOutboundWriter Owner { get; } = owner;
        public ReadOnlyMemory<byte> Message { get; } = message;
        public CancellationToken Ct { get; } = ct;
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Claims the frame for writing (or faulting); loses to an earlier withdrawal.</summary>
        public bool TryActivate() => Interlocked.CompareExchange(ref _state, 1, 0) == 0;

        /// <summary>Claims the frame for cancellation-before-write; loses once a writer activated it.</summary>
        public bool TryWithdraw() => Interlocked.CompareExchange(ref _state, 2, 0) == 0;
    }
}
