using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Security.Claims;
using System.Text;
using BenchmarkDotNet.Attributes;
using Elarion.Abstractions.Connections;
using Elarion.Connections;
using Elarion.Connections.Tcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Elarion.Benchmarks.Connections;

/// <summary>
/// Framing microbenchmarks: per-message cost of the delimited framer's read (scan + slice, expected
/// allocation-free) and write (delimiters + payload into a reused buffer writer).
/// </summary>
[MemoryDiagnoser]
public class TcpFramingBenchmarks {
    private const int Messages = 1_000;

    private readonly DelimitedTcpFramer _framer = new((byte)'>', (byte)'<');
    private readonly ArrayBufferWriter<byte> _writer = new(64 * 1024);
    private byte[] _wire = null!;
    private byte[] _payload = null!;

    [GlobalSetup]
    public void Setup() {
        _payload = Encoding.UTF8.GetBytes(new string('x', 30));
        var writer = new ArrayBufferWriter<byte>();
        for (var i = 0; i < Messages; i++) _framer.WriteMessage(_payload, writer);

        _wire = writer.WrittenMemory.ToArray();
    }

    [Benchmark(OperationsPerInvoke = Messages)]
    public int ReadDelimited() {
        var buffer = (ReadOnlyMemory<byte>)_wire;
        var count = 0;
        while (_framer.TryReadMessage(buffer, out var consumed, out _)) {
            buffer = buffer[consumed..];
            count++;
        }

        return count;
    }

    [Benchmark(OperationsPerInvoke = Messages)]
    public int WriteDelimited() {
        _writer.ResetWrittenCount();
        for (var i = 0; i < Messages; i++) _framer.WriteMessage(_payload, _writer);

        return _writer.WrittenCount;
    }
}

/// <summary>
/// End-to-end TCP server throughput over real loopback sockets, 32-byte delimited messages: a hand-rolled
/// minimal socket read loop (the floor — accept, read, count delimiters, nothing else) against the full
/// adapter pipeline (framer → codec dispatch → counter), in three flavors: raw-slice delivery (zero-copy),
/// a codec that decodes each message to a string (the priced text convenience, paid in the codec where it
/// belongs), and raw delivery with a 60&#160;s idle window armed (never firing — measures the idle
/// machinery's hot-path cost, which the sync-completion fast path is supposed to make ~zero).
/// </summary>
[MemoryDiagnoser]
public class TcpServerBenchmarks {
    private const int MessagesPerInvoke = 10_000;
    private const int MessagesPerBatch = 100;
    private const byte Delimiter = (byte)'\n';

    private readonly MessageCounter _counter = new();
    private ServiceProvider _provider = null!;
    private IHostedService[] _hosted = null!;
    private CancellationTokenSource _rawServerCts = null!;
    private TcpListener _rawListener = null!;
    private Task _rawServerLoop = null!;
    private NetworkStream _rawClient = null!;
    private NetworkStream _binaryClient = null!;
    private NetworkStream _binaryIdleClient = null!;
    private NetworkStream _textClient = null!;
    private byte[] _batch = null!;

    [GlobalSetup]
    public async Task Setup() {
        var message = new byte[32];
        Array.Fill(message, (byte)'x', 0, 31);
        message[31] = Delimiter;
        _batch = new byte[message.Length * MessagesPerBatch];
        for (var i = 0; i < MessagesPerBatch; i++) message.CopyTo(_batch, i * message.Length);

        // The floor: accept one socket, read chunks, count delimiter bytes. No framing, no dispatch.
        _rawServerCts = new CancellationTokenSource();
        _rawListener = new TcpListener(IPAddress.Loopback, 0);
        _rawListener.Start();
        _rawServerLoop = RunRawServerAsync(_rawListener, _counter, _rawServerCts.Token);

        var binaryPort = new TaskCompletionSource<IPEndPoint>(TaskCreationOptions.RunContinuationsAsynchronously);
        var binaryIdlePort = new TaskCompletionSource<IPEndPoint>(TaskCreationOptions.RunContinuationsAsynchronously);
        var textPort = new TaskCompletionSource<IPEndPoint>(TaskCreationOptions.RunContinuationsAsynchronously);

        var services = new ServiceCollection();
        services.AddElarionConnections();
        services.AddSingleton(_counter);
        services.AddSingleton<CountingConnectionHandler>();
        services.AddSingleton<DecodingConnectionHandler>();
        services.AddElarionTcpConnectionListener<CountingConnectionHandler>(o => {
            o.ListenEndPoint = new IPEndPoint(IPAddress.Loopback, 0);
            o.Framer = new DelimitedTcpFramer(Delimiter);
            o.OnListening = binaryPort.SetResult;
        });
        services.AddElarionTcpConnectionListener<CountingConnectionHandler>(o => {
            o.ListenEndPoint = new IPEndPoint(IPAddress.Loopback, 0);
            o.Framer = new DelimitedTcpFramer(Delimiter);
            o.IdleTimeout = TimeSpan.FromSeconds(60);
            o.OnListening = binaryIdlePort.SetResult;
        });
        services.AddElarionTcpConnectionListener<DecodingConnectionHandler>(o => {
            o.ListenEndPoint = new IPEndPoint(IPAddress.Loopback, 0);
            o.Framer = new DelimitedTcpFramer(Delimiter);
            o.OnListening = textPort.SetResult;
        });
        _provider = services.BuildServiceProvider();
        _hosted = [.. _provider.GetServices<IHostedService>()];
        foreach (var service in _hosted) await service.StartAsync(CancellationToken.None);

        _rawClient = await ConnectAsync((IPEndPoint)_rawListener.LocalEndpoint);
        _binaryClient = await ConnectAsync(await binaryPort.Task);
        _binaryIdleClient = await ConnectAsync(await binaryIdlePort.Task);
        _textClient = await ConnectAsync(await textPort.Task);
    }

    [GlobalCleanup]
    public async Task Cleanup() {
        _rawClient.Dispose();
        _binaryClient.Dispose();
        _binaryIdleClient.Dispose();
        _textClient.Dispose();
        await _rawServerCts.CancelAsync();
        _rawListener.Stop();
        await _rawServerLoop;
        foreach (var service in _hosted) await service.StopAsync(CancellationToken.None);

        await _provider.DisposeAsync();
        _rawServerCts.Dispose();
    }

    [Benchmark(Baseline = true, OperationsPerInvoke = MessagesPerInvoke)]
    public Task RawSocketFloor() {
        return PumpAsync(_rawClient);
    }

    [Benchmark(OperationsPerInvoke = MessagesPerInvoke)]
    public Task Adapter_Binary() {
        return PumpAsync(_binaryClient);
    }

    [Benchmark(OperationsPerInvoke = MessagesPerInvoke)]
    public Task Adapter_Binary_IdleArmed() {
        return PumpAsync(_binaryIdleClient);
    }

    [Benchmark(OperationsPerInvoke = MessagesPerInvoke)]
    public Task Adapter_TextDecode() {
        return PumpAsync(_textClient);
    }

    private async Task PumpAsync(NetworkStream client) {
        var done = _counter.WaitFor(MessagesPerInvoke);
        for (var sent = 0; sent < MessagesPerInvoke; sent += MessagesPerBatch) await client.WriteAsync(_batch);

        await done;
    }

    private static async Task<NetworkStream> ConnectAsync(IPEndPoint endPoint) {
        var client = new TcpClient { NoDelay = true };
        await client.ConnectAsync(endPoint);
        return client.GetStream();
    }

    private static async Task RunRawServerAsync(TcpListener listener, MessageCounter counter, CancellationToken ct) {
        try {
            using var client = await listener.AcceptTcpClientAsync(ct);
            var stream = client.GetStream();
            var buffer = new byte[64 * 1024];
            while (!ct.IsCancellationRequested) {
                var read = await stream.ReadAsync(buffer, ct);
                if (read == 0) return;

                counter.Add(buffer.AsSpan(0, read).Count(Delimiter));
            }
        }
        catch (OperationCanceledException) {
        }
        catch (SocketException) {
        }
    }

    /// <summary>Signals a waiter when N further messages were observed (one live benchmark at a time).</summary>
    public sealed class MessageCounter {
        private long _count;
        private long _target = long.MaxValue;
        private TaskCompletionSource _signal = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task WaitFor(int messages) {
            _signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Volatile.Write(ref _target, Interlocked.Read(ref _count) + messages);
            return _signal.Task;
        }

        public void Add(int messages) {
            if (Interlocked.Add(ref _count, messages) >= Volatile.Read(ref _target)) {
                Volatile.Write(ref _target, long.MaxValue);
                _signal.TrySetResult();
            }
        }
    }

    /// <summary>No-handshake ticket (identity by binding) and a codec that only counts.</summary>
    public sealed class CountingConnectionHandler(MessageCounter counter) : TcpConnectionHandler {
        public override ValueTask<ClientConnectionTicket?> AuthenticateAsync(
            TcpHandshakeContext handshake, CancellationToken ct) {
            // Authenticated tickets require a principal id — id-less ones are rejected at registration.
            return ValueTask.FromResult<ClientConnectionTicket?>(new ClientConnectionTicket {
                Principal = new ClaimsPrincipal(new ClaimsIdentity("bench")),
                PrincipalId = "bench-device"
            });
        }

        public override IClientConnectionProtocol CreateProtocol(TcpClientConnection connection) {
            return new CountingProtocol(counter);
        }
    }

    /// <summary>Same, but the codec decodes each message to a string first — the priced text convenience,
    /// now living where it belongs (in the codec).</summary>
    public sealed class DecodingConnectionHandler(MessageCounter counter) : TcpConnectionHandler {
        public override ValueTask<ClientConnectionTicket?> AuthenticateAsync(
            TcpHandshakeContext handshake, CancellationToken ct) {
            return ValueTask.FromResult<ClientConnectionTicket?>(new ClientConnectionTicket {
                Principal = new ClaimsPrincipal(new ClaimsIdentity("bench")),
                PrincipalId = "bench-device"
            });
        }

        public override IClientConnectionProtocol CreateProtocol(TcpClientConnection connection) {
            return new DecodingProtocol(counter);
        }
    }

    private sealed class CountingProtocol(MessageCounter counter) : IClientConnectionProtocol {
        public ValueTask OnBinaryAsync(ReadOnlyMemory<byte> message, CancellationToken ct) {
            counter.Add(1);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class DecodingProtocol(MessageCounter counter) : IClientConnectionProtocol {
        public ValueTask OnBinaryAsync(ReadOnlyMemory<byte> message, CancellationToken ct) {
            counter.Add(Encoding.UTF8.GetString(message.Span).Length > 0 ? 1 : 0);
            return ValueTask.CompletedTask;
        }
    }
}
