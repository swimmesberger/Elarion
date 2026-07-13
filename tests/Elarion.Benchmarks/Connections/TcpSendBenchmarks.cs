using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Security.Claims;
using BenchmarkDotNet.Attributes;
using Elarion.Abstractions.Connections;
using Elarion.Connections;
using Elarion.Connections.Tcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Elarion.Benchmarks.Connections;

/// <summary>
/// Send-path throughput over real loopback sockets — the proxy/forwarder scenario, where a gateway's hot
/// loop is receive-on-one-link → <c>SendBinaryAsync</c>-on-another and the send side must be as cheap as
/// the receive side: a hand-rolled framed write into a reused buffer (the floor) against the adapter's
/// per-connection sink. The peer drains and counts delimiters so every invoke measures full delivery of
/// 32-byte framed messages.
/// </summary>
[MemoryDiagnoser]
public class TcpSendBenchmarks {
    private const int MessagesPerInvoke = 10_000;
    private const byte Delimiter = (byte)'\n';

    private readonly TcpServerBenchmarks.MessageCounter _received = new();
    private readonly ArrayBufferWriter<byte> _floorWriter = new(4 * 1024);
    private ServiceProvider _provider = null!;
    private IHostedService[] _hosted = null!;
    private TcpClient _adapterClient = null!;
    private Task _adapterDrainLoop = null!;
    private TcpListener _rawListener = null!;
    private TcpClient _rawClient = null!;
    private TcpClient _rawAccepted = null!;
    private NetworkStream _rawSendStream = null!;
    private Task _rawDrainLoop = null!;
    private TcpClientConnection _sink = null!;
    private byte[] _payload = null!;
    private DelimitedTcpFramer _framer = null!;

    [GlobalSetup]
    public async Task Setup() {
        _payload = new byte[31];
        Array.Fill(_payload, (byte)'x');
        _framer = new DelimitedTcpFramer(Delimiter);

        // Adapter side: a listener whose connection we grab via an observer; the benchmark sends through
        // its sink, and the connected client drains + counts.
        var sinkReady = new TaskCompletionSource<TcpClientConnection>(TaskCreationOptions.RunContinuationsAsynchronously);
        var port = new TaskCompletionSource<IPEndPoint>(TaskCreationOptions.RunContinuationsAsynchronously);
        var services = new ServiceCollection();
        services.AddElarionConnections();
        services.AddSingleton<IClientConnectionObserver>(new SinkCapture(sinkReady));
        services.AddSingleton<PassiveHandler>();
        services.AddElarionTcpConnectionListener<PassiveHandler>(o => {
            o.ListenEndPoint = new IPEndPoint(IPAddress.Loopback, 0);
            o.Framer = _framer;
            o.OnListening = port.SetResult;
        });
        _provider = services.BuildServiceProvider();
        _hosted = [.. _provider.GetServices<IHostedService>()];
        foreach (var service in _hosted) {
            await service.StartAsync(CancellationToken.None);
        }

        _adapterClient = new TcpClient { NoDelay = true };
        await _adapterClient.ConnectAsync(await port.Task);
        _adapterDrainLoop = DrainAsync(_adapterClient.GetStream(), _received);
        _sink = await sinkReady.Task;

        // The floor: a plain connected socket pair; sends frame into a reused buffer and write directly.
        _rawListener = new TcpListener(IPAddress.Loopback, 0);
        _rawListener.Start();
        var rawConnect = new TcpClient { NoDelay = true };
        var rawAccept = _rawListener.AcceptTcpClientAsync();
        await rawConnect.ConnectAsync((IPEndPoint)_rawListener.LocalEndpoint);
        _rawClient = rawConnect;
        _rawAccepted = await rawAccept;
        _rawSendStream = _rawClient.GetStream();
        _rawDrainLoop = DrainAsync(_rawAccepted.GetStream(), _received);
    }

    [GlobalCleanup]
    public async Task Cleanup() {
        _adapterClient.Dispose();
        _rawClient.Dispose();
        _rawAccepted.Dispose();
        _rawListener.Stop();
        try {
            await Task.WhenAll(_adapterDrainLoop, _rawDrainLoop).WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception) {
            // Drain loops end with their sockets; a straggler must not fail cleanup.
        }

        foreach (var service in _hosted) {
            await service.StopAsync(CancellationToken.None);
        }

        await _provider.DisposeAsync();
    }

    [Benchmark(Baseline = true, OperationsPerInvoke = MessagesPerInvoke)]
    public async Task RawFramedWriteFloor() {
        var done = _received.WaitFor(MessagesPerInvoke);
        for (var i = 0; i < MessagesPerInvoke; i++) {
            _floorWriter.ResetWrittenCount();
            _framer.WriteMessage(_payload, _floorWriter);
            await _rawSendStream.WriteAsync(_floorWriter.WrittenMemory);
        }

        await done;
    }

    [Benchmark(OperationsPerInvoke = MessagesPerInvoke)]
    public async Task Sink_SendBinary() {
        var done = _received.WaitFor(MessagesPerInvoke);
        for (var i = 0; i < MessagesPerInvoke; i++) {
            await _sink.SendBinaryAsync(_payload);
        }

        await done;
    }

    private static async Task DrainAsync(NetworkStream stream, TcpServerBenchmarks.MessageCounter counter) {
        var buffer = new byte[64 * 1024];
        try {
            while (true) {
                var read = await stream.ReadAsync(buffer);
                if (read == 0) {
                    return;
                }

                counter.Add(buffer.AsSpan(0, read).Count(Delimiter));
            }
        }
        catch (IOException) {
        }
        catch (ObjectDisposedException) {
        }
    }

    private sealed class SinkCapture(TaskCompletionSource<TcpClientConnection> ready) : IClientConnectionObserver {
        public ValueTask OnConnectedAsync(IClientConnectionSink connection, CancellationToken ct = default) {
            ready.TrySetResult((TcpClientConnection)connection);
            return ValueTask.CompletedTask;
        }

        public ValueTask OnDisconnectedAsync(ClientConnection connection, CancellationToken ct = default) =>
            ValueTask.CompletedTask;
    }

    public sealed class PassiveHandler : TcpConnectionHandler {
        public override ValueTask<ClientConnectionTicket?> AuthenticateAsync(
            TcpHandshakeContext handshake, CancellationToken ct) =>
            ValueTask.FromResult<ClientConnectionTicket?>(new ClientConnectionTicket {
                Principal = new ClaimsPrincipal(new ClaimsIdentity(authenticationType: "bench")),
            });

        public override IClientConnectionProtocol CreateProtocol(TcpClientConnection connection) =>
            new SilentProtocol();
    }

    private sealed class SilentProtocol : IClientConnectionProtocol {
        public ValueTask OnBinaryAsync(ReadOnlyMemory<byte> message, CancellationToken ct) =>
            ValueTask.CompletedTask;
    }
}
