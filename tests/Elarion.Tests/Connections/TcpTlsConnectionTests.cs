using System.Buffers;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using AwesomeAssertions;
using Elarion.Abstractions.Connections;
using Elarion.Connections;
using Elarion.Connections.Simulation;
using Elarion.Connections.Tcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Elarion.Tests.Connections;

/// <summary>Exercises the TCP adapter's TLS transport upgrade before any framed application processing.</summary>
public sealed class TcpTlsConnectionTests {
    [Fact]
    public async Task ListenerTls_AuthenticatesFramesRegistersAndExchangesMessages() {
        var ct = TestContext.Current.CancellationToken;
        using var certificate = CreateLoopbackCertificate();
        var handler = new TlsChallengeHandler();
        await using var host = await StartListenerAsync(handler, o => o.Tls = ServerTls(certificate), ct);
        using var client = await ConnectAsync(host.EndPoint, ct);
        await using var stream = await AuthenticateClientAsync(client, "localhost", TrustAnyCertificate, ct);

        (await ReadLineAsync(stream, ct)).Should().Be("challenge");
        await WriteLineAsync(stream, "device:tls-1", ct);
        (await ReadLineAsync(stream, ct)).Should().Be("welcome");
        (await host.Observer.Connected.Task.WaitAsync(ct)).Connection.PrincipalId.Should().Be("tls-1");

        await WriteLineAsync(stream, "ping", ct);
        (await ReadLineAsync(stream, ct)).Should().Be("echo:ping");

        client.Close();
        await host.Observer.Disconnected.Task.WaitAsync(ct);
        host.Registry.Connections.Should().BeEmpty();
    }

    [Fact]
    public async Task ListenerTls_DefaultPlatformValidationRejectsSelfSignedCertificate() {
        var ct = TestContext.Current.CancellationToken;
        using var certificate = CreateLoopbackCertificate();
        var handler = new TlsChallengeHandler();
        await using var host = await StartListenerAsync(handler, o => o.Tls = ServerTls(certificate), ct);
        using var client = await ConnectAsync(host.EndPoint, ct);
        await using var stream = new SslStream(client.GetStream(), true);

        var authenticate = async () => await stream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions {
            TargetHost = "localhost",
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
        }, ct);
        await authenticate.Should().ThrowAsync<AuthenticationException>();

        handler.AuthenticationCalls.Should().Be(0);
        host.Registry.Connections.Should().BeEmpty();
    }

    [Fact]
    public async Task ListenerTls_HostnameRejectionDoesNotReachApplicationHandshake() {
        var ct = TestContext.Current.CancellationToken;
        using var certificate = CreateLoopbackCertificate();
        var handler = new TlsChallengeHandler();
        await using var host = await StartListenerAsync(handler, o => o.Tls = ServerTls(certificate), ct);
        using var client = await ConnectAsync(host.EndPoint, ct);
        await using var stream = new SslStream(client.GetStream(), true);

        var validationErrors = new TaskCompletionSource<SslPolicyErrors>(TaskCreationOptions.RunContinuationsAsynchronously);
        var authenticate = async () => await stream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions {
            TargetHost = "not-localhost",
            RemoteCertificateValidationCallback = (_, _, _, errors) => {
                validationErrors.TrySetResult(errors);
                return false;
            },
        }, ct);
        await authenticate.Should().ThrowAsync<AuthenticationException>();
        (await validationErrors.Task.WaitAsync(ct)).Should().HaveFlag(SslPolicyErrors.RemoteCertificateNameMismatch);

        handler.AuthenticationCalls.Should().Be(0);
        host.Registry.Connections.Should().BeEmpty();
    }

    [Fact]
    public async Task ListenerTls_SilentPeerTimesOutBeforeFramingOrAuthentication() {
        var ct = TestContext.Current.CancellationToken;
        using var certificate = CreateLoopbackCertificate();
        var handler = new TlsChallengeHandler();
        await using var host = await StartListenerAsync(handler,
            o => o.Tls = ServerTls(certificate, TimeSpan.FromMilliseconds(100)), ct);
        using var client = await ConnectAsync(host.EndPoint, ct);
        var buffer = new byte[1];

        (await client.GetStream().ReadAsync(buffer, ct)).Should().Be(0);
        handler.AuthenticationCalls.Should().Be(0);
        host.Registry.Connections.Should().BeEmpty();
    }

    [Fact]
    public async Task ListenerTls_PlaintextBytesNeverReachFramingOrAuthentication() {
        var ct = TestContext.Current.CancellationToken;
        using var certificate = CreateLoopbackCertificate();
        var handler = new TlsChallengeHandler();
        await using var host = await StartListenerAsync(handler, o => o.Tls = ServerTls(certificate), ct);
        using var client = await ConnectAsync(host.EndPoint, ct);
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.UTF8.GetBytes("plaintext\n"), ct);

        var buffer = new byte[1];
        (await stream.ReadAsync(buffer, ct)).Should().Be(0);
        handler.AuthenticationCalls.Should().Be(0);
        host.Registry.Connections.Should().BeEmpty();
    }

    [Fact]
    public async Task PerConnectionTlsOverride_UpgradesBeforeApplicationBytes() {
        var ct = TestContext.Current.CancellationToken;
        using var certificate = CreateLoopbackCertificate();
        var policyCreated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tls = ServerTls(certificate, factoryObserved: policyCreated);
        var handler = new PerConnectionTlsHandler(tls);
        await using var host = await StartListenerAsync(handler, _ => { }, ct);
        using var client = await ConnectAsync(host.EndPoint, ct);
        await using var stream = await AuthenticateClientAsync(client, "localhost", TrustAnyCertificate, ct);

        await policyCreated.Task.WaitAsync(ct);
        (await ReadLineAsync(stream, ct)).Should().Be("challenge");
        await WriteLineAsync(stream, "device:override", ct);
        (await ReadLineAsync(stream, ct)).Should().Be("welcome");
        handler.AuthenticationCalls.Should().Be(1);
    }

    [Fact]
    public async Task InvalidPerConnectionTlsDirectionIsRejectedBeforeFactoryOrApplicationHandshake() {
        var ct = TestContext.Current.CancellationToken;
        var handler = new InvalidDirectionHandler();
        await using var host = await StartListenerAsync(handler, _ => { }, ct);
        using var client = await ConnectAsync(host.EndPoint, ct);
        var stream = client.GetStream();
        var buffer = new byte[1];

        (await stream.ReadAsync(buffer, ct)).Should().Be(0);
        handler.ConfigureCalls.Should().Be(1);
        handler.FactoryCalls.Should().Be(0);
        handler.AuthenticationCalls.Should().Be(0);
        host.Registry.Connections.Should().BeEmpty();
    }

    private static TcpServerTlsOptions ServerTls(
        X509Certificate2 certificate,
        TimeSpan? timeout = null,
        TaskCompletionSource? factoryObserved = null) => new() {
        HandshakeTimeout = timeout ?? TimeSpan.FromSeconds(2),
        CreateAuthenticationOptionsAsync = (_, _) => {
            factoryObserved?.TrySetResult();
            return ValueTask.FromResult(new SslServerAuthenticationOptions {
                ServerCertificate = certificate,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            });
        },
    };

    private static async Task<TcpTlsTestHost> StartListenerAsync(
        TcpConnectionHandler handler,
        Action<ElarionTcpListenerOptions> configure,
        CancellationToken ct) {
        var bound = new TaskCompletionSource<IPEndPoint>(TaskCreationOptions.RunContinuationsAsynchronously);
        var services = new ServiceCollection();
        services.AddElarionConnections();
        services.AddSingleton<AwaitableConnectionObserver>();
        services.AddSingleton<IClientConnectionObserver>(sp => sp.GetRequiredService<AwaitableConnectionObserver>());
        services.AddElarionTcpConnectionListener<TlsChallengeHandler>(o => {
            o.ListenEndPoint = new IPEndPoint(IPAddress.Loopback, 0);
            o.Framer = new DelimitedTcpFramer((byte)'\n');
            o.OnListening = bound.SetResult;
            configure(o);
        });

        // Register the exact supplied handler under the service type the generic hosted service resolves.
        services.AddSingleton<TlsChallengeHandler>(_ => handler as TlsChallengeHandler
            ?? throw new InvalidOperationException("The supplied test handler must derive from TlsChallengeHandler."));
        var provider = services.BuildServiceProvider();
        var hosted = provider.GetServices<IHostedService>().ToArray();
        foreach (var service in hosted) {
            await service.StartAsync(ct);
        }

        return new TcpTlsTestHost(provider, hosted, await bound.Task.WaitAsync(ct));
    }

    private static async Task<TcpClient> ConnectAsync(IPEndPoint endpoint, CancellationToken ct) {
        var client = new TcpClient();
        await client.ConnectAsync(endpoint, ct);
        return client;
    }

    private static async Task<SslStream> AuthenticateClientAsync(
        TcpClient client,
        string targetHost,
        RemoteCertificateValidationCallback callback,
        CancellationToken ct) {
        var stream = new SslStream(client.GetStream(), true);
        await stream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions {
            TargetHost = targetHost,
            RemoteCertificateValidationCallback = callback,
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
        }, ct);
        return stream;
    }

    private static bool TrustAnyCertificate(
        object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors) => true;

    private static X509Certificate2 CreateLoopbackCertificate() {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(san.Build());
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    }

    private static async Task WriteLineAsync(Stream stream, string line, CancellationToken ct) =>
        await stream.WriteAsync(Encoding.UTF8.GetBytes(line + "\n"), ct);

    private static async Task<string?> ReadLineAsync(Stream stream, CancellationToken ct) {
        var buffer = new List<byte>();
        var one = new byte[1];
        while (true) {
            var read = await stream.ReadAsync(one, ct);
            if (read == 0) {
                return null;
            }

            if (one[0] == (byte)'\n') {
                return Encoding.UTF8.GetString([.. buffer]);
            }

            buffer.Add(one[0]);
        }
    }

    private sealed class TcpTlsTestHost(
        ServiceProvider provider,
        IReadOnlyList<IHostedService> hosted,
        IPEndPoint endPoint) : IAsyncDisposable {
        public IPEndPoint EndPoint { get; } = endPoint;
        public IClientConnectionRegistry Registry => provider.GetRequiredService<IClientConnectionRegistry>();
        public AwaitableConnectionObserver Observer => provider.GetRequiredService<AwaitableConnectionObserver>();

        public async ValueTask DisposeAsync() {
            foreach (var service in hosted) {
                await service.StopAsync(CancellationToken.None);
            }

            await provider.DisposeAsync();
        }
    }

    private class TlsChallengeHandler : TcpConnectionHandler {
        public int AuthenticationCalls { get; private set; }

        public override async ValueTask<ClientConnectionTicket?> AuthenticateAsync(
            TcpHandshakeContext handshake,
            CancellationToken ct) {
            AuthenticationCalls++;
            await handshake.SendTextAsync("challenge", ct);
            var response = await handshake.ReceiveTextAsync(ct);
            if (response is null || !response.StartsWith("device:", StringComparison.Ordinal)) {
                return null;
            }

            await handshake.SendTextAsync("welcome", ct);
            return new ClientConnectionTicket {
                Principal = new ClaimsPrincipal(new ClaimsIdentity("device")),
                PrincipalId = response["device:".Length..],
            };
        }

        public override IClientConnectionProtocol CreateProtocol(TcpClientConnection connection) =>
            new EchoProtocol(connection);
    }

    private sealed class PerConnectionTlsHandler(TcpServerTlsOptions tls) : TlsChallengeHandler {
        public override ValueTask<TcpConnectionSettings?> ConfigureConnectionAsync(
            TcpConnectionPeer peer,
            CancellationToken ct) => ValueTask.FromResult<TcpConnectionSettings?>(new TcpConnectionSettings { Tls = tls });
    }

    private sealed class InvalidDirectionHandler : TlsChallengeHandler {
        public int ConfigureCalls { get; private set; }
        public int FactoryCalls { get; private set; }

        public override ValueTask<TcpConnectionSettings?> ConfigureConnectionAsync(
            TcpConnectionPeer peer,
            CancellationToken ct) {
            ConfigureCalls++;
            return ValueTask.FromResult<TcpConnectionSettings?>(new TcpConnectionSettings {
                Tls = new TcpClientTlsOptions {
                    CreateAuthenticationOptionsAsync = (_, _) => {
                        FactoryCalls++;
                        return ValueTask.FromResult(new SslClientAuthenticationOptions { TargetHost = "localhost" });
                    },
                },
            });
        }
    }

    private sealed class EchoProtocol(TcpClientConnection connection) : IClientConnectionProtocol {
        public ValueTask OnBinaryAsync(ReadOnlyMemory<byte> message, CancellationToken ct) =>
            connection.SendTextAsync("echo:" + Encoding.UTF8.GetString(message.Span), ct);
    }
}
