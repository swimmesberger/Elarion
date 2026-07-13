using AwesomeAssertions;
using Elarion.Abstractions.Connections;
using Elarion.Connections;
using Elarion.Connections.Diagnostics;
using Elarion.Connections.Simulation;
using Elarion.Tests.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Elarion.Tests.Connections;

/// <summary>
/// Covers the node-local connection registry default: unique-id registration (duplicate throws), idempotent
/// unregistration, observer dispatch ordering (after the index mutation) and isolation (one failing observer
/// neither breaks the lifecycle edge nor starves its peers), and the by-principal lookup.
/// </summary>
public sealed class ClientConnectionRegistryTests {
    [Fact]
    public async Task RegisterAsync_WithDuplicateConnectionId_Throws() {
        var ct = TestContext.Current.CancellationToken;
        await using var provider = BuildProvider();
        var registry = provider.GetRequiredService<IClientConnectionRegistry>();
        await registry.RegisterAsync(Sink("conn-1", "user-1"), ct);

        var register = async () => await registry.RegisterAsync(Sink("conn-1", "user-1"), ct);

        await register.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task UnregisterAsync_IsIdempotent_ObserverNotifiedOnce() {
        var ct = TestContext.Current.CancellationToken;
        var observer = new RecordingObserver();
        await using var provider = BuildProvider(observer);
        var registry = provider.GetRequiredService<IClientConnectionRegistry>();
        await registry.RegisterAsync(Sink("conn-1", "user-1"), ct);

        await registry.UnregisterAsync("conn-1", ct);
        await registry.UnregisterAsync("conn-1", ct);
        await registry.UnregisterAsync("never-registered", ct);

        observer.Disconnected.Should().ContainSingle().Which.ConnectionId.Should().Be("conn-1");
    }

    [Fact]
    public async Task Observers_RunAfterTheIndexMutation() {
        var ct = TestContext.Current.CancellationToken;
        var observer = new LookupProbingObserver();
        await using var provider = BuildProvider(observer);
        var registry = provider.GetRequiredService<IClientConnectionRegistry>();
        observer.Attach(registry);

        await registry.RegisterAsync(Sink("conn-1", "user-1"), ct);
        observer.VisibleOnConnect.Should().BeTrue();

        await registry.UnregisterAsync("conn-1", ct);
        observer.VisibleOnDisconnect.Should().BeFalse();
    }

    [Fact]
    public async Task ObserverFailure_IsIsolated_PeersStillNotified() {
        var ct = TestContext.Current.CancellationToken;
        var recording = new RecordingObserver();
        await using var provider = BuildProvider(new ThrowingObserver(), recording);
        var registry = provider.GetRequiredService<IClientConnectionRegistry>();

        await registry.RegisterAsync(Sink("conn-1", "user-1"), ct);
        await registry.UnregisterAsync("conn-1", ct);

        recording.Connected.Should().ContainSingle();
        recording.Disconnected.Should().ContainSingle();
    }

    [Fact]
    public async Task GetForPrincipal_FiltersByPrincipalId() {
        var ct = TestContext.Current.CancellationToken;
        await using var provider = BuildProvider();
        var registry = provider.GetRequiredService<IClientConnectionRegistry>();
        await registry.RegisterAsync(Sink("conn-1", "user-1"), ct);
        await registry.RegisterAsync(Sink("conn-2", "user-1"), ct);
        await registry.RegisterAsync(Sink("conn-3", "user-2"), ct);

        registry.GetForPrincipal("user-1").Select(c => c.Connection.ConnectionId)
            .Should().BeEquivalentTo(["conn-1", "conn-2"]);
        registry.GetForPrincipal("user-3").Should().BeEmpty();
        registry.Connections.Should().HaveCount(3);
        registry.TryGet("conn-3", out var found).Should().BeTrue();
        found!.Connection.PrincipalId.Should().Be("user-2");
    }

    [Fact]
    public async Task Lifecycle_EmitsOpenedActiveAndClosedMeasurements() {
        var ct = TestContext.Current.CancellationToken;
        using var meters = new MeterCollector(ConnectionTelemetry.MeterName);
        await using var provider = BuildProvider();
        var registry = provider.GetRequiredService<IClientConnectionRegistry>();

        await registry.RegisterAsync(Sink("conn-t", "user-1"), ct);
        await registry.UnregisterAsync("conn-t", ct);

        var byInstrument = meters.Measurements
            .Where(m => m.HasTag("elarion.connection.transport", "test"))
            .ToLookup(m => m.InstrumentName, m => (long)m.Value);
        byInstrument["connection.opened"].Sum().Should().Be(1);
        byInstrument["connection.closed"].Sum().Should().Be(1);
        byInstrument["connection.active"].Sum().Should().Be(0);
    }

    private static ServiceProvider BuildProvider(params IClientConnectionObserver[] observers) {
        var services = new ServiceCollection();
        services.AddElarionConnections();
        foreach (var observer in observers) {
            services.AddSingleton(observer);
        }
        return services.BuildServiceProvider();
    }

    internal static SimulatedClientConnection Sink(string connectionId, string principalId) =>
        new(principalId: principalId, connectionId: connectionId);

    private sealed class RecordingObserver : IClientConnectionObserver {
        public List<ClientConnection> Connected { get; } = [];
        public List<ClientConnection> Disconnected { get; } = [];

        public ValueTask OnConnectedAsync(IClientConnectionSink connection, CancellationToken ct = default) {
            Connected.Add(connection.Connection);
            return ValueTask.CompletedTask;
        }

        public ValueTask OnDisconnectedAsync(ClientConnection connection, CancellationToken ct = default) {
            Disconnected.Add(connection);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class LookupProbingObserver : IClientConnectionObserver {
        private IClientConnectionRegistry? _registry;

        public bool? VisibleOnConnect { get; private set; }
        public bool? VisibleOnDisconnect { get; private set; }

        public void Attach(IClientConnectionRegistry registry) => _registry = registry;

        public ValueTask OnConnectedAsync(IClientConnectionSink connection, CancellationToken ct = default) {
            VisibleOnConnect = _registry!.TryGet(connection.Connection.ConnectionId, out _);
            return ValueTask.CompletedTask;
        }

        public ValueTask OnDisconnectedAsync(ClientConnection connection, CancellationToken ct = default) {
            VisibleOnDisconnect = _registry!.TryGet(connection.ConnectionId, out _);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ThrowingObserver : IClientConnectionObserver {
        public ValueTask OnConnectedAsync(IClientConnectionSink connection, CancellationToken ct = default) =>
            throw new InvalidOperationException("observer failure");

        public ValueTask OnDisconnectedAsync(ClientConnection connection, CancellationToken ct = default) =>
            throw new InvalidOperationException("observer failure");
    }
}
