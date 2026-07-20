using System.Buffers;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
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
    private TcpClientConnection _saturatedSink = null!;
    private Task _saturatedSend = null!;
    private TcpConnectionLifetime _saturatedLifetime = null!;
    private byte[] _payload = null!;
    private DelimitedTcpFramer _framer = null!;
    private ServiceProvider _tlsProvider = null!;
    private IHostedService[] _tlsHosted = null!;
    private X509Certificate2 _tlsCertificate = null!;
    private TcpClient _tlsClient = null!;
    private SslStream _tlsStream = null!;
    private Task _tlsDrainLoop = null!;
    private TcpClientConnection _tlsSink = null!;

    [GlobalSetup]
    public async Task Setup() {
        _payload = new byte[31];
        Array.Fill(_payload, (byte)'x');
        _framer = new DelimitedTcpFramer(Delimiter);

        // Adapter side: a listener whose connection we grab via an observer; the benchmark sends through
        // its sink, and the connected client drains + counts.
        var sinkReady =
            new TaskCompletionSource<TcpClientConnection>(TaskCreationOptions.RunContinuationsAsynchronously);
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
        foreach (var service in _hosted) await service.StartAsync(CancellationToken.None);

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

        // The saturated sink: capacity 1 over a stream whose write never completes — one admitted send
        // pins the queue full, so every benchmarked send exercises the rejection path.
        var blocked = new NeverCompletingWriteStream();
        var identity = new ClientConnection {
            ConnectionId = "saturated",
            Transport = "bench",
            Principal = new ClaimsPrincipal(new ClaimsIdentity("bench")),
            ConnectedAt = DateTimeOffset.UtcNow
        };
        _saturatedLifetime = new TcpConnectionLifetime(blocked, CancellationToken.None);
        var writer = new TcpOutboundWriter(
            blocked, _framer, 4 * 1024, 64 * 1024, 1,
            identity.ConnectionId, identity.Transport, _saturatedLifetime);
        _saturatedLifetime.AttachWriter(writer);
        _saturatedSink = new TcpClientConnection(identity, writer, _saturatedLifetime, null);
        _saturatedSend = _saturatedSink.SendBinaryAsync(_payload).AsTask();

        // TLS variant: same passive endpoint behind a TLS upgrade — measures the steady-state overhead
        // of the encrypted leg (record framing + encryption), not the one-time handshake.
        _tlsCertificate = CreateLoopbackCertificate();
        var tlsSinkReady =
            new TaskCompletionSource<TcpClientConnection>(TaskCreationOptions.RunContinuationsAsynchronously);
        var tlsPort = new TaskCompletionSource<IPEndPoint>(TaskCreationOptions.RunContinuationsAsynchronously);
        var tlsServices = new ServiceCollection();
        tlsServices.AddElarionConnections();
        tlsServices.AddSingleton<IClientConnectionObserver>(new SinkCapture(tlsSinkReady));
        tlsServices.AddSingleton<PassiveHandler>();
        tlsServices.AddElarionTcpConnectionListener<PassiveHandler>(o => {
            o.ListenEndPoint = new IPEndPoint(IPAddress.Loopback, 0);
            o.Framer = _framer;
            o.OnListening = tlsPort.SetResult;
            o.Tls = new TcpServerTlsOptions {
                CreateAuthenticationOptionsAsync = (_, _) => ValueTask.FromResult(
                    new SslServerAuthenticationOptions { ServerCertificate = _tlsCertificate })
            };
        });
        _tlsProvider = tlsServices.BuildServiceProvider();
        _tlsHosted = [.. _tlsProvider.GetServices<IHostedService>()];
        foreach (var service in _tlsHosted) await service.StartAsync(CancellationToken.None);

        _tlsClient = new TcpClient { NoDelay = true };
        await _tlsClient.ConnectAsync(await tlsPort.Task);
        _tlsStream = new SslStream(_tlsClient.GetStream(), false,
            // Benchmark-only trust-any for the self-signed loopback certificate.
            (_, _, _, _) => true);
        await _tlsStream.AuthenticateAsClientAsync("localhost");
        _tlsDrainLoop = DrainAsync(_tlsStream, _received);
        _tlsSink = await tlsSinkReady.Task;
    }

    [GlobalCleanup]
    public async Task Cleanup() {
        _saturatedLifetime.Abort(null);
        try {
            await _saturatedSend;
        }
        catch (ClientConnectionClosedException) {
            // The pinned send settles through the abort — expected.
        }

        _adapterClient.Dispose();
        _rawClient.Dispose();
        _rawAccepted.Dispose();
        _rawListener.Stop();
        await _tlsStream.DisposeAsync();
        _tlsClient.Dispose();
        try {
            await Task.WhenAll(_adapterDrainLoop, _rawDrainLoop, _tlsDrainLoop).WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception) {
            // Drain loops end with their sockets; a straggler must not fail cleanup.
        }

        foreach (var service in _hosted) await service.StopAsync(CancellationToken.None);

        foreach (var service in _tlsHosted) await service.StopAsync(CancellationToken.None);

        await _provider.DisposeAsync();
        await _tlsProvider.DisposeAsync();
        _tlsCertificate.Dispose();
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
        for (var i = 0; i < MessagesPerInvoke; i++) await _sink.SendBinaryAsync(_payload);

        await done;
    }

    // The writer-based send (ADR-0066): the payload is serialized straight into the shared frame buffer
    // between the framer's prologue/epilogue — the delta against Sink_SendBinary is what skipping the
    // caller-materialized payload buffer is worth on the uncontended path.
    [Benchmark(OperationsPerInvoke = MessagesPerInvoke)]
    public async Task Sink_SendWriter() {
        var done = _received.WaitFor(MessagesPerInvoke);
        for (var i = 0; i < MessagesPerInvoke; i++)
            await _sink.SendBinaryAsync(_payload, static (payload, output) => output.Write(payload), default);

        await done;
    }

    // The contended writer-send: producers beyond the inline writer serialize into rented per-send buffers
    // and queue — the delta against Sink_SendBinary_FourProducers is the rented-buffer cost under contention.
    [Benchmark(OperationsPerInvoke = MessagesPerInvoke)]
    public async Task Sink_SendWriter_FourProducers() {
        var done = _received.WaitFor(MessagesPerInvoke);
        var producers = new Task[4];
        for (var p = 0; p < producers.Length; p++)
            producers[p] = Task.Run(async () => {
                for (var i = 0; i < MessagesPerInvoke / 4; i++)
                    await _sink.SendBinaryAsync(
                        _payload, static (payload, output) => output.Write(payload), default);
            });

        await Task.WhenAll(producers);
        await done;
    }

    // Concurrent producers below capacity: the contended path — the first sender writes inline, the rest
    // queue FIFO behind it and a drainer settles them after each physical write.
    [Benchmark(OperationsPerInvoke = MessagesPerInvoke)]
    public async Task Sink_SendBinary_FourProducers() {
        var done = _received.WaitFor(MessagesPerInvoke);
        var producers = new Task[4];
        for (var p = 0; p < producers.Length; p++)
            producers[p] = Task.Run(async () => {
                for (var i = 0; i < MessagesPerInvoke / 4; i++) await _sink.SendBinaryAsync(_payload);
            });

        await Task.WhenAll(producers);
        await done;
    }

    // The TLS leg: identical send loop through a connection whose stream is an SslStream — the delta
    // against Sink_SendBinary is the per-message cost of TLS record framing and encryption.
    [Benchmark(OperationsPerInvoke = MessagesPerInvoke)]
    public async Task Sink_SendBinary_Tls() {
        var done = _received.WaitFor(MessagesPerInvoke);
        for (var i = 0; i < MessagesPerInvoke; i++) await _tlsSink.SendBinaryAsync(_payload);

        await done;
    }

    // Deterministic saturation: a standalone sink whose single admitted send never completes, so every
    // benchmarked send is rejected at capacity — measures the cost of the deterministic saturation fault
    // (no frame allocation, no queueing).
    [Benchmark(OperationsPerInvoke = 1_000)]
    public async Task<int> Sink_SaturatedRejection() {
        var rejections = 0;
        for (var i = 0; i < 1_000; i++)
            try {
                await _saturatedSink.SendBinaryAsync(_payload);
            }
            catch (TcpSendQueueFullException) {
                rejections++;
            }

        return rejections;
    }

    private static async Task DrainAsync(Stream stream, TcpServerBenchmarks.MessageCounter counter) {
        var buffer = new byte[64 * 1024];
        try {
            while (true) {
                var read = await stream.ReadAsync(buffer);
                if (read == 0) return;

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

        public ValueTask OnDisconnectedAsync(ClientConnection connection, CancellationToken ct = default) {
            return ValueTask.CompletedTask;
        }

        public ValueTask OnIdentityPromotedAsync(
            ClientConnection previous,
            ClientConnection current,
            CancellationToken ct = default) {
            return ValueTask.CompletedTask;
        }
    }

    public sealed class PassiveHandler : TcpConnectionHandler {
        public override ValueTask<TcpConnectionSession?> CreateSessionAsync(
            TcpConnectionPeer peer, CancellationToken ct) {
            return ValueTask.FromResult<TcpConnectionSession?>(new PassiveSession());
        }
    }

    private sealed class PassiveSession : TcpConnectionSession {
        public override ValueTask<ClientConnectionTicket?> AuthenticateAsync(
            TcpHandshakeContext handshake, CancellationToken ct) {
            // Authenticated tickets require a principal id — an id-less authenticated ticket is
            // rejected at registration and the setup's sink capture would wait forever.
            return ValueTask.FromResult<ClientConnectionTicket?>(new ClientConnectionTicket {
                Principal = new ClaimsPrincipal(new ClaimsIdentity("bench")),
                PrincipalId = "bench-device"
            });
        }

        public override IClientConnectionProtocol CreateProtocol(TcpClientConnection connection) {
            return new SilentProtocol();
        }
    }

    private sealed class SilentProtocol : IClientConnectionProtocol {
        public ValueTask OnBinaryAsync(ReadOnlyMemory<byte> message, CancellationToken ct) {
            return ValueTask.CompletedTask;
        }
    }

    private static X509Certificate2 CreateLoopbackCertificate() {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(san.Build());
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    }

    /// <summary>Writes never complete until disposal faults them — pins one admitted send so the
    /// saturation benchmark's queue stays deterministically full.</summary>
    private sealed class NeverCompletingWriteStream : Stream {
        private readonly TaskCompletionSource _blocked = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) {
            await _blocked.Task;
        }

        protected override void Dispose(bool disposing) {
            _blocked.TrySetException(new ObjectDisposedException(nameof(NeverCompletingWriteStream)));
            base.Dispose(disposing);
        }

        public override bool CanRead => false;
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
}
