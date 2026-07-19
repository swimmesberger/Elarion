using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Security.Claims;
using System.Text;
using AwesomeAssertions;
using Elarion.Abstractions.Connections;
using Elarion.Connections;
using Elarion.Connections.Simulation;
using Elarion.Connections.Tcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Elarion.Tests.Connections;

/// <summary>
/// Covers the deterministic TCP close/lifetime controller (first reason wins, idempotent close, forced
/// abort after the grace period, no abandoned runner tasks, registry empty before endpoint stop returns)
/// and the bounded FIFO outbound writer (admission capacity including in-progress work, deterministic
/// saturation with no frame allocation, completion only after the physical stream write, FIFO ordering
/// under contention, withdrawal on cancellation before the write, connection abort on cancellation during
/// the write, fault fan-out to active and queued sends, graceful drain, and exactly-once settlement).
/// Barriers, not sleeps: gate streams signal write entry and are released explicitly.
/// </summary>
public sealed class TcpConnectionLifecycleTests {
    [Fact]
    public void Lifetime_FirstCloseReasonWins_AndLaterInitiatorsAreNoOps() {
        using var lifetime = new TcpConnectionLifetime(new DisposeRecorder(), CancellationToken.None);
        var first = new InvalidOperationException("first");

        lifetime.TryBeginClose(first).Should().BeTrue();
        lifetime.TryBeginClose(new InvalidOperationException("second")).Should().BeFalse();
        lifetime.TryBeginClose(null).Should().BeFalse();

        lifetime.CloseReason.Should().BeSameAs(first);
        lifetime.ReceiveToken.IsCancellationRequested.Should().BeTrue();
    }

    [Fact]
    public void Lifetime_RepeatedCloseAndAbort_AreIdempotent_AndDisposeTransportOnce() {
        var transport = new DisposeRecorder();
        using var lifetime = new TcpConnectionLifetime(transport, CancellationToken.None);

        lifetime.RequestGracefulClose(null);
        lifetime.RequestGracefulClose(null);
        lifetime.WasForced.Should().BeFalse();

        lifetime.Abort(null);
        lifetime.Abort(new InvalidOperationException("late"));
        lifetime.WasForced.Should().BeTrue();
        lifetime.CloseReason.Should().BeNull();
        transport.Disposals.Should().Be(1);

        lifetime.DisposeTransport();
        transport.Disposals.Should().Be(1);
    }

    [Fact]
    public async Task Send_CompletesOnlyAfterThePhysicalStreamWrite() {
        var ct = TestContext.Current.CancellationToken;
        await using var stream = new GatedWriteStream();
        var (connection, _) = TcpConnectionAdapterTests.CreateStandaloneConnection(
            stream, new DelimitedTcpFramer((byte)'\n'), 16, 64);

        var send = connection.SendTextAsync("one", ct).AsTask();
        await stream.WaitForEnteredWritesAsync(1, ct);
        send.IsCompleted.Should().BeFalse();

        stream.ReleaseOne();
        await send.WaitAsync(ct);
        stream.CompletedWrites.Should().ContainSingle();
    }

    [Fact]
    public async Task Sends_UnderContention_WriteInFifoAdmissionOrder() {
        var ct = TestContext.Current.CancellationToken;
        await using var stream = new GatedWriteStream();
        var (connection, _) = TcpConnectionAdapterTests.CreateStandaloneConnection(
            stream, new DelimitedTcpFramer((byte)'\n'), 16, 64);

        // The first send becomes the inline writer and blocks in the stream; the rest are admitted in
        // order behind it while it is provably in flight.
        var first = connection.SendTextAsync("m-0", ct).AsTask();
        await stream.WaitForEnteredWritesAsync(1, ct);
        var queued = new List<Task>();
        for (var i = 1; i <= 3; i++) queued.Add(connection.SendTextAsync($"m-{i}", ct).AsTask());

        stream.ReleaseAll();
        await Task.WhenAll([first, .. queued]).WaitAsync(ct);
        // Admission order is preserved on the wire; the drainer coalesces the queued frames into one
        // physical write behind the inline first frame.
        string.Concat(stream.CompletedWriteTexts).Should().Be("m-0\nm-1\nm-2\nm-3\n");
        stream.CompletedWrites.Should().HaveCount(2);
    }

    [Fact]
    public async Task Saturation_FailsDeterministically_CountsInProgressWork_AndAllocatesNoFrame() {
        var ct = TestContext.Current.CancellationToken;
        await using var stream = new GatedWriteStream();
        var framer = new CountingFramer();
        var (connection, _) = TcpConnectionAdapterTests.CreateStandaloneConnection(
            stream, framer, 16, 64, 2);

        // Capacity 2 = one in-progress physical write + one queued frame.
        var active = connection.SendBinaryAsync(new byte[] { 1 }, ct).AsTask();
        await stream.WaitForEnteredWritesAsync(1, ct);
        var queued = connection.SendBinaryAsync(new byte[] { 2 }, ct).AsTask();
        var framedBeforeRejection = framer.WriteCalls;

        var saturated = async () => await connection.SendBinaryAsync(new byte[] { 3 }, ct);
        var rejection = await saturated.Should().ThrowAsync<TcpSendQueueFullException>();
        rejection.Which.Capacity.Should().Be(2);
        framer.WriteCalls.Should().Be(framedBeforeRejection);

        stream.ReleaseAll();
        await Task.WhenAll(active, queued).WaitAsync(ct);
        stream.CompletedWrites.Should().HaveCount(2);
    }

    [Fact]
    public async Task CancellationBeforeDequeue_WithdrawsTheFrame_AndEmitsNothing() {
        var ct = TestContext.Current.CancellationToken;
        await using var stream = new GatedWriteStream();
        var (connection, _) = TcpConnectionAdapterTests.CreateStandaloneConnection(
            stream, new DelimitedTcpFramer((byte)'\n'), 16, 64);

        var first = connection.SendTextAsync("kept", ct).AsTask();
        await stream.WaitForEnteredWritesAsync(1, ct);
        using var withdrawal = new CancellationTokenSource();
        var withdrawn = connection.SendTextAsync("withdrawn", withdrawal.Token).AsTask();
        var last = connection.SendTextAsync("last", ct).AsTask();

        await withdrawal.CancelAsync();
        await ((Func<Task>)(() => withdrawn)).Should().ThrowAsync<OperationCanceledException>();

        stream.ReleaseAll();
        await Task.WhenAll(first, last).WaitAsync(ct);
        stream.CompletedWriteTexts.Should().Equal("kept\n", "last\n");
    }

    [Fact]
    public async Task CancellationDuringThePhysicalWrite_AbortsTheConnection() {
        var ct = TestContext.Current.CancellationToken;
        await using var stream = new GatedWriteStream();
        var (connection, lifetime) = TcpConnectionAdapterTests.CreateStandaloneConnection(
            stream, new DelimitedTcpFramer((byte)'\n'), 16, 64);

        using var cancellation = new CancellationTokenSource();
        var send = connection.SendTextAsync("partial", cancellation.Token).AsTask();
        await stream.WaitForEnteredWritesAsync(1, ct);

        // A frame may be partially on the wire — the stream can no longer carry boundaries, so the
        // connection must die rather than desync the peer.
        await cancellation.CancelAsync();
        await ((Func<Task>)(() => send)).Should().ThrowAsync<OperationCanceledException>();
        lifetime.WasForced.Should().BeTrue();
        stream.Disposed.Should().BeTrue();

        var next = async () => await connection.SendTextAsync("after", ct);
        await next.Should().ThrowAsync<ClientConnectionClosedException>();
    }

    [Fact]
    public async Task PeerFailure_FaultsTheActiveAndEveryQueuedSend() {
        var ct = TestContext.Current.CancellationToken;
        await using var stream = new GatedWriteStream();
        var (connection, lifetime) = TcpConnectionAdapterTests.CreateStandaloneConnection(
            stream, new DelimitedTcpFramer((byte)'\n'), 16, 64);

        var active = connection.SendTextAsync("active", ct).AsTask();
        await stream.WaitForEnteredWritesAsync(1, ct);
        var queuedFirst = connection.SendTextAsync("queued-1", ct).AsTask();
        var queuedSecond = connection.SendTextAsync("queued-2", ct).AsTask();

        stream.FailAll(new IOException("connection reset by peer"));

        await ((Func<Task>)(() => active)).Should().ThrowAsync<ClientConnectionClosedException>();
        await ((Func<Task>)(() => queuedFirst)).Should().ThrowAsync<ClientConnectionClosedException>();
        await ((Func<Task>)(() => queuedSecond)).Should().ThrowAsync<ClientConnectionClosedException>();
        lifetime.WasForced.Should().BeTrue();
    }

    [Fact]
    public async Task GracefulClose_RejectsNewSends_DrainsAdmittedOnes_AndSettlesEveryoneExactlyOnce() {
        var ct = TestContext.Current.CancellationToken;
        await using var stream = new GatedWriteStream();
        var (connection, lifetime) = TcpConnectionAdapterTests.CreateStandaloneConnection(
            stream, new DelimitedTcpFramer((byte)'\n'), 16, 64);

        var active = connection.SendTextAsync("draining-0", ct).AsTask();
        await stream.WaitForEnteredWritesAsync(1, ct);
        var queued = connection.SendTextAsync("draining-1", ct).AsTask();

        await connection.CloseAsync();
        var rejected = async () => await connection.SendTextAsync("late", ct);
        await rejected.Should().ThrowAsync<ClientConnectionClosedException>();

        // The admitted sends were not faulted by the close request — they drain to completion.
        stream.ReleaseAll();
        await Task.WhenAll(active, queued).WaitAsync(ct);
        stream.CompletedWriteTexts.Should().Equal("draining-0\n", "draining-1\n");
        lifetime.WasForced.Should().BeFalse();
    }

    [Fact]
    public async Task Abort_FaultsQueuedSends_AndDrainCompletionStillResolves() {
        var ct = TestContext.Current.CancellationToken;
        await using var stream = new GatedWriteStream();
        var (connection, lifetime) = TcpConnectionAdapterTests.CreateStandaloneConnection(
            stream, new DelimitedTcpFramer((byte)'\n'), 16, 64);

        var active = connection.SendTextAsync("active", ct).AsTask();
        await stream.WaitForEnteredWritesAsync(1, ct);
        var queued = connection.SendTextAsync("queued", ct).AsTask();

        lifetime.Abort(new InvalidOperationException("endpoint teardown"));

        // The queued frame faults immediately; the active write faults through the disposed transport.
        await ((Func<Task>)(() => queued)).Should().ThrowAsync<ClientConnectionClosedException>();
        await ((Func<Task>)(() => active)).Should().ThrowAsync<ClientConnectionClosedException>();
        lifetime.WasForced.Should().BeTrue();
    }

    [Fact]
    public async Task Runner_BlockedReadIgnoringCancellation_IsForcedDownAfterTheGracePeriod() {
        var ct = TestContext.Current.CancellationToken;
        await using var provider = new ServiceCollection().AddElarionConnections().BuildServiceProvider();
        var registry = provider.GetRequiredService<IClientConnectionRegistry>();
        var handler = new ImmediateTicketHandler();
        await using var stream = new BlockedReadStream();
        var live = new TcpLiveConnectionSet();
        var options = new ElarionTcpConnectionOptions {
            Framer = new DelimitedTcpFramer((byte)'\n'),
            ShutdownGracePeriod = TimeSpan.FromMilliseconds(100)
        };

        var run = TcpConnectionRunner.RunAsync(
            stream, new TcpConnectionPeer(null, null), stream, null, options, handler,
            registry, null, TimeProvider.System, NullLogger.Instance,
            CancellationToken.None, live);
        await handler.Opened.Task.WaitAsync(ct);
        registry.Connections.Should().ContainSingle();

        // The endpoint-stop sequence: graceful close first; the read ignores cancellation, so the grace
        // period elapses and the raw transport is aborted — the runner still completes and unregisters.
        live.RequestGracefulCloseAll();
        try {
            await run.WaitAsync(options.ShutdownGracePeriod, TimeProvider.System, ct);
        }
        catch (TimeoutException) {
            live.AbortAll();
        }

        await run.WaitAsync(ct);
        handler.Protocol!.ClosedCalls.Should().Be(1);
        registry.Connections.Should().BeEmpty();
    }

    [Fact]
    public async Task ListenerStop_WithLiveConnection_ReturnsWithEmptyRegistry_WithoutForcingTheClose() {
        var ct = TestContext.Current.CancellationToken;
        var boundEndPoint = new TaskCompletionSource<IPEndPoint>(TaskCreationOptions.RunContinuationsAsynchronously);
        var services = new ServiceCollection();
        services.AddElarionConnections();
        services.AddSingleton<GreetingTicketHandler>();
        services.AddSingleton<AwaitableConnectionObserver>();
        services.AddSingleton<IClientConnectionObserver>(sp => sp.GetRequiredService<AwaitableConnectionObserver>());
        services.AddElarionTcpConnectionListener<GreetingTicketHandler>(o => {
            o.ListenEndPoint = new IPEndPoint(IPAddress.Loopback, 0);
            o.Framer = new DelimitedTcpFramer((byte)'\n');
            o.OnListening = boundEndPoint.SetResult;
        });
        await using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<IClientConnectionRegistry>();
        var hosted = provider.GetServices<IHostedService>().ToArray();
        foreach (var service in hosted) await service.StartAsync(ct);

        using var client = new TcpClient();
        await client.ConnectAsync(await boundEndPoint.Task.WaitAsync(ct), ct);
        var clientStream = client.GetStream();
        await clientStream.WriteAsync("hello\n"u8.ToArray(), ct);
        var welcome = new byte[64];
        (await clientStream.ReadAsync(welcome, ct)).Should().BePositive();
        // The client sees "welcome" before the server task registers — wait for the observer signal.
        await provider.GetRequiredService<AwaitableConnectionObserver>().Connected.Task.WaitAsync(ct);
        registry.Connections.Should().ContainSingle();
        var handler = provider.GetRequiredService<GreetingTicketHandler>();

        // The client stays connected: stop must close the connection itself, run the full ordered
        // teardown, and leave the registry empty before StopAsync returns — without the forced path.
        foreach (var service in hosted) await service.StopAsync(CancellationToken.None);

        registry.Connections.Should().BeEmpty();
        handler.Protocol!.ClosedCalls.Should().Be(1);
        handler.Protocol.CloseReason.Should().BeNull();
    }

    [Fact]
    public async Task WriterSend_ProducesIdenticalWireBytes_ForBothShippedFramers() {
        var ct = TestContext.Current.CancellationToken;
        foreach (TcpMessageFramer framer in new TcpMessageFramer[] {
                     new LengthPrefixedTcpFramer(), new DelimitedTcpFramer((byte)'\n', (byte)'>')
                 }) {
            await using var memoryWire = new MemoryStream();
            await using var writerWire = new MemoryStream();
            var (memoryConnection, _) = TcpConnectionAdapterTests.CreateStandaloneConnection(
                memoryWire, framer, 16, 64);
            var (writerConnection, _) = TcpConnectionAdapterTests.CreateStandaloneConnection(
                writerWire, framer, 16, 64);
            var payload = "hello-writer"u8.ToArray();

            await memoryConnection.SendBinaryAsync(payload, ct);
            await writerConnection.SendBinaryAsync(payload, static (bytes, output) => output.Write(bytes), ct);

            writerWire.ToArray().Should().Equal(memoryWire.ToArray());
        }
    }

    [Fact]
    public async Task WriterSend_DelimiterViolation_FaultsTheSendButTheConnectionLives() {
        var ct = TestContext.Current.CancellationToken;
        await using var wire = new MemoryStream();
        var (connection, lifetime) = TcpConnectionAdapterTests.CreateStandaloneConnection(
            wire, new DelimitedTcpFramer((byte)'\n'), 16, 64);

        var violating = async () => await connection.SendBinaryAsync(
            0, static (_, output) => output.Write("with\nnewline"u8), ct);
        await violating.Should().ThrowAsync<ArgumentException>();

        lifetime.ReceiveToken.IsCancellationRequested.Should().BeFalse();
        await connection.SendTextAsync("still-alive", ct);
        wire.ToArray().Should().Equal("still-alive\n"u8.ToArray());
    }

    [Fact]
    public async Task WriterSend_OversizedPayload_FaultsNonFatally_AndReleasesTheSlot() {
        var ct = TestContext.Current.CancellationToken;
        await using var wire = new MemoryStream();
        var (connection, lifetime) = TcpConnectionAdapterTests.CreateStandaloneConnection(
            wire, new LengthPrefixedTcpFramer(), 16, 32);

        var oversized = async () => await connection.SendBinaryAsync(
            0, static (_, output) => output.Write(new byte[128]), ct);
        await oversized.Should().ThrowAsync<TcpOutboundFrameTooLargeException>();

        lifetime.ReceiveToken.IsCancellationRequested.Should().BeFalse();
        await connection.SendBinaryAsync(0, static (_, output) => output.Write("fits"u8), ct);
        wire.ToArray().Should().EndWith("fits"u8.ToArray());
    }

    [Fact]
    public async Task WriterSend_SerializeCallbackThrow_FaultsOnlyThatSend() {
        var ct = TestContext.Current.CancellationToken;
        await using var wire = new MemoryStream();
        var (connection, lifetime) = TcpConnectionAdapterTests.CreateStandaloneConnection(
            wire, new LengthPrefixedTcpFramer(), 16, 64);

        var throwing = async () => await connection.SendBinaryAsync<int>(
            0, static (_, _) => throw new InvalidOperationException("serializer bug"), ct);
        await throwing.Should().ThrowAsync<InvalidOperationException>().WithMessage("serializer bug");

        lifetime.ReceiveToken.IsCancellationRequested.Should().BeFalse();
        await connection.SendBinaryAsync(0, static (_, output) => output.Write("ok"u8), ct);
        wire.Length.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task WriterSend_UnderContention_QueuesWithFifoOrderAndSharedSaturation() {
        var ct = TestContext.Current.CancellationToken;
        await using var stream = new GatedWriteStream();
        var (connection, _) = TcpConnectionAdapterTests.CreateStandaloneConnection(
            stream, new DelimitedTcpFramer((byte)'\n'), 16, 64, 3);

        // The first writer-send becomes the inline writer (serialized straight into the shared frame
        // buffer) and blocks in the stream; contended writer-sends serialize into rented per-send buffers
        // and queue behind it. Saturation counts both paths identically.
        var first = connection.SendBinaryAsync("w-0", static (s, o) => o.Write(Encoding.UTF8.GetBytes(s)), ct)
            .AsTask();
        await stream.WaitForEnteredWritesAsync(1, ct);
        var second = connection.SendBinaryAsync("w-1", static (s, o) => o.Write(Encoding.UTF8.GetBytes(s)), ct)
            .AsTask();
        var third = connection.SendTextAsync("w-2", ct).AsTask();
        var saturated = async () => await connection.SendBinaryAsync(
            "w-3", static (s, o) => o.Write(Encoding.UTF8.GetBytes(s)), ct);
        await saturated.Should().ThrowAsync<TcpSendQueueFullException>();

        stream.ReleaseAll();
        await Task.WhenAll(first, second, third).WaitAsync(ct);
        string.Concat(stream.CompletedWriteTexts).Should().Be("w-0\nw-1\nw-2\n");
    }

    [Fact]
    public async Task WriterSend_EnqueuedAfterTheInlineWriterReleasedOwnership_StillDrains() {
        // Regression: a contended writer-send serializes OUTSIDE the gate, so the inline writer that made
        // it contended can finish meanwhile, find an empty queue, and release stream ownership. The late
        // enqueue must claim ownership itself — before the fix it queued into an ownerless queue and its
        // completion never settled (deadlock caught by the four-producer send benchmark).
        var ct = TestContext.Current.CancellationToken;
        await using var stream = new GatedWriteStream();
        var (connection, _) = TcpConnectionAdapterTests.CreateStandaloneConnection(
            stream, new DelimitedTcpFramer((byte)'\n'), 16, 64);
        using var enteredSerialize = new SemaphoreSlim(0);
        using var releaseSerialize = new SemaphoreSlim(0);

        // First send becomes the inline writer and blocks in the stream; the second is admitted as
        // contended and parks inside its serialize callback (on a pool thread — the callback runs
        // synchronously inside SendAsync).
        var first = connection.SendTextAsync("first", ct).AsTask();
        await stream.WaitForEnteredWritesAsync(1, ct);
        var second = Task.Run(() => connection.SendBinaryAsync(
            (Entered: enteredSerialize, Release: releaseSerialize),
            static (gates, output) => {
                gates.Entered.Release();
                gates.Release.Wait();
                output.Write("late"u8);
            }, ct).AsTask(), ct);
        await enteredSerialize.WaitAsync(ct);

        // Let the inline writer finish and release ownership while the contended send is still serializing.
        stream.ReleaseAll();
        await first.WaitAsync(ct);

        releaseSerialize.Release();
        await second.WaitAsync(TimeSpan.FromSeconds(10), ct);
        string.Concat(stream.CompletedWriteTexts).Should().Be("first\nlate\n");
    }

    [Fact]
    public async Task WriterSend_QueuedCancellation_WithdrawsBeforeTheWire() {
        var ct = TestContext.Current.CancellationToken;
        await using var stream = new GatedWriteStream();
        var (connection, _) = TcpConnectionAdapterTests.CreateStandaloneConnection(
            stream, new DelimitedTcpFramer((byte)'\n'), 16, 64);

        var first = connection.SendTextAsync("keep", ct).AsTask();
        await stream.WaitForEnteredWritesAsync(1, ct);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var queued = connection.SendBinaryAsync(
            "withdrawn", static (s, o) => o.Write(Encoding.UTF8.GetBytes(s)), cancellation.Token).AsTask();
        cancellation.Cancel();

        var withdrawal = async () => await queued;
        await withdrawal.Should().ThrowAsync<OperationCanceledException>();

        stream.ReleaseAll();
        await first.WaitAsync(ct);
        string.Concat(stream.CompletedWriteTexts).Should().Be("keep\n");
    }

    private sealed class DisposeRecorder : IDisposable {
        public int Disposals { get; private set; }

        public void Dispose() {
            Disposals++;
        }
    }

    /// <summary>A write-side stream whose writes block until released — the barrier the writer tests use
    /// instead of sleeps. Reads never complete (these tests drive the outbound leg only).</summary>
    private sealed class GatedWriteStream : Stream {
        private readonly Lock _gate = new();
        private readonly Queue<PendingWrite> _entered = new();
        private readonly List<byte[]> _completed = [];
        private TaskCompletionSource _enteredSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _enteredCount;
        private bool _releaseEverything;
        private Exception? _failure;

        public bool Disposed { get; private set; }

        public IReadOnlyList<byte[]> CompletedWrites {
            get {
                lock (_gate) {
                    return [.. _completed];
                }
            }
        }

        public IReadOnlyList<string> CompletedWriteTexts =>
            [.. CompletedWrites.Select(static bytes => System.Text.Encoding.UTF8.GetString(bytes))];

        public async Task WaitForEnteredWritesAsync(int count, CancellationToken ct) {
            while (true) {
                Task pending;
                lock (_gate) {
                    if (_enteredCount >= count) return;

                    pending = _enteredSignal.Task;
                }

                await pending.WaitAsync(ct);
            }
        }

        public void ReleaseOne() {
            PendingWrite? next;
            lock (_gate) {
                _entered.TryDequeue(out next);
            }

            next?.Release.TrySetResult();
        }

        public void ReleaseAll() {
            List<PendingWrite> pending;
            lock (_gate) {
                _releaseEverything = true;
                pending = [.. _entered];
                _entered.Clear();
            }

            foreach (var write in pending) write.Release.TrySetResult();
        }

        public void FailAll(Exception failure) {
            List<PendingWrite> pending;
            lock (_gate) {
                _failure = failure;
                pending = [.. _entered];
                _entered.Clear();
            }

            foreach (var write in pending) write.Release.TrySetException(failure);
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) {
            var write = new PendingWrite(buffer.ToArray());
            TaskCompletionSource entered;
            lock (_gate) {
                if (_failure is not null) throw _failure;

                if (Disposed) throw new ObjectDisposedException(nameof(GatedWriteStream));

                _enteredCount++;
                if (!_releaseEverything)
                    _entered.Enqueue(write);
                else
                    write.Release.TrySetResult();

                entered = _enteredSignal;
                _enteredSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            entered.TrySetResult();
            await write.Release.Task.WaitAsync(ct);
            lock (_gate) {
                _completed.Add(write.Bytes);
            }
        }

        protected override void Dispose(bool disposing) {
            List<PendingWrite> pending;
            lock (_gate) {
                Disposed = true;
                pending = [.. _entered];
                _entered.Clear();
            }

            foreach (var write in pending)
                write.Release.TrySetException(new ObjectDisposedException(nameof(GatedWriteStream)));

            base.Dispose(disposing);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();

        public override long Position {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) {
            return new ValueTask<int>(new TaskCompletionSource<int>().Task);
        }

        public override void Flush() {
        }

        public override int Read(byte[] buffer, int offset, int count) {
            throw new NotSupportedException();
        }

        public override long Seek(long offset, SeekOrigin origin) {
            throw new NotSupportedException();
        }

        public override void SetLength(long value) {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count) {
            WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        }

        private sealed class PendingWrite(byte[] bytes) {
            public byte[] Bytes { get; } = bytes;
            public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    /// <summary>A stream whose read ignores cancellation until disposal faults it — the rogue transport
    /// that only the forced abort can unblock.</summary>
    private sealed class BlockedReadStream : Stream {
        private readonly TaskCompletionSource<int> _blockedRead =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) {
            return new ValueTask<int>(_blockedRead.Task);
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) {
            return ValueTask.CompletedTask;
        }

        protected override void Dispose(bool disposing) {
            _blockedRead.TrySetException(new ObjectDisposedException(nameof(BlockedReadStream)));
            base.Dispose(disposing);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();

        public override long Position {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() {
        }

        public override int Read(byte[] buffer, int offset, int count) {
            throw new NotSupportedException();
        }

        public override long Seek(long offset, SeekOrigin origin) {
            throw new NotSupportedException();
        }

        public override void SetLength(long value) {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count) {
            throw new NotSupportedException();
        }
    }

    private sealed class CountingFramer : TcpMessageFramer {
        public int WriteCalls { get; private set; }

        public override bool TryReadMessage(
            ReadOnlyMemory<byte> buffer, out int consumed, out ReadOnlyMemory<byte> message) {
            consumed = 0;
            message = default;
            return false;
        }

        public override void WriteMessage(ReadOnlySpan<byte> payload, IBufferWriter<byte> output) {
            WriteCalls++;
            output.Write(payload);
        }

        public override int BeginMessage(IBufferWriter<byte> output) {
            return 0;
        }

        public override void CompleteMessage(Span<byte> prologue, ReadOnlySpan<byte> payload,
            IBufferWriter<byte> output) {
            WriteCalls++;
        }
    }

    /// <summary>Tickets the connection without any framed exchange, so a test controls the link purely
    /// through its stream.</summary>
    private sealed class ImmediateTicketHandler : TcpConnectionHandler {
        public TaskCompletionSource Opened { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public RecordingLifecycleProtocol? Protocol { get; private set; }

        public override ValueTask<ClientConnectionTicket?> AuthenticateAsync(
            TcpHandshakeContext handshake, CancellationToken ct) {
            // Authenticated tickets require a principal id — an id-less authenticated ticket is rejected
            // at registration by the registry's identity normalization.
            return ValueTask.FromResult<ClientConnectionTicket?>(new ClientConnectionTicket {
                Principal = new ClaimsPrincipal(new ClaimsIdentity("test")),
                PrincipalId = "lifecycle-device"
            });
        }

        public override IClientConnectionProtocol CreateProtocol(TcpClientConnection connection) {
            return Protocol = new RecordingLifecycleProtocol(Opened);
        }
    }

    private sealed class GreetingTicketHandler : TcpConnectionHandler {
        public RecordingLifecycleProtocol? Protocol { get; private set; }

        public override async ValueTask<ClientConnectionTicket?> AuthenticateAsync(
            TcpHandshakeContext handshake, CancellationToken ct) {
            var greeting = await handshake.ReceiveTextAsync(ct);
            if (greeting is null) return null;

            await handshake.SendTextAsync("welcome", ct);
            return new ClientConnectionTicket {
                Principal = new ClaimsPrincipal(new ClaimsIdentity("test")),
                PrincipalId = "greeting-device"
            };
        }

        public override IClientConnectionProtocol CreateProtocol(TcpClientConnection connection) {
            return Protocol = new RecordingLifecycleProtocol(
                new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        }
    }

    private sealed class RecordingLifecycleProtocol(TaskCompletionSource opened) : IClientConnectionProtocol {
        public int ClosedCalls { get; private set; }

        public Exception? CloseReason { get; private set; }

        public ValueTask OnOpenedAsync(ClientConnection connection, CancellationToken ct) {
            opened.TrySetResult();
            return ValueTask.CompletedTask;
        }

        public ValueTask OnBinaryAsync(ReadOnlyMemory<byte> message, CancellationToken ct) {
            return ValueTask.CompletedTask;
        }

        public ValueTask OnClosedAsync(ClientConnection connection, Exception? reason, CancellationToken ct) {
            ClosedCalls++;
            CloseReason = reason;
            return ValueTask.CompletedTask;
        }
    }
}
