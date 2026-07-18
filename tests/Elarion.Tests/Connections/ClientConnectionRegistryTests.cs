using System.Security.Claims;
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
    public async Task RegisterAsync_DuplicateId_DoesNotNormalizeRejectedSink() {
        var ct = TestContext.Current.CancellationToken;
        await using var provider = BuildProvider();
        var registry = provider.GetRequiredService<IClientConnectionRegistry>();
        await registry.RegisterAsync(Sink("conn-1", "user-1"), ct);
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "user-2")], authenticationType: "test"));
        var metadata = new Dictionary<string, string> { ["source"] = "original" };
        var rejected = new SimulatedClientConnection(
            principalId: "user-2", connectionId: "conn-1", principal: principal, metadata: metadata);
        var original = rejected.Connection;

        var register = async () => await registry.RegisterAsync(rejected, ct);

        await register.Should().ThrowAsync<InvalidOperationException>();
        rejected.Connection.Should().BeSameAs(original);
        rejected.Connection.Principal.Should().BeSameAs(principal);
        rejected.Connection.Metadata.Should().BeSameAs(metadata);
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
    public async Task PromoteAsync_AnonymousConnection_UpdatesLookupAndRevision() {
        var ct = TestContext.Current.CancellationToken;
        await using var provider = BuildProvider();
        var registry = provider.GetRequiredService<IClientConnectionRegistry>();
        var sink = AnonymousSink("conn-1");
        var connectedAt = sink.Connection.ConnectedAt;
        await registry.RegisterAsync(sink, ct);

        var status = await registry.PromoteAsync("conn-1", AuthenticatedIdentity("user-1"), ct);

        status.Should().Be(ClientConnectionPromotionStatus.Promoted);
        sink.Connection.PrincipalId.Should().Be("user-1");
        sink.Connection.IdentityRevision.Should().Be(1);
        sink.Connection.ConnectedAt.Should().Be(connectedAt);
        registry.GetForPrincipal("user-1").Should().ContainSingle().Which.Should().BeSameAs(sink);
    }

    [Fact]
    public async Task PromoteAsync_ConcurrentAttempts_HasExactlyOneWinner() {
        var ct = TestContext.Current.CancellationToken;
        await using var provider = BuildProvider();
        var registry = provider.GetRequiredService<IClientConnectionRegistry>();
        var sink = AnonymousSink("conn-1");
        await registry.RegisterAsync(sink, ct);

        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = Task.Run(async () => {
            await start.Task;
            return await registry.PromoteAsync("conn-1", AuthenticatedIdentity("user-1"), ct);
        }, ct);
        var second = Task.Run(async () => {
            await start.Task;
            return await registry.PromoteAsync("conn-1", AuthenticatedIdentity("user-2"), ct);
        }, ct);
        start.SetResult();

        var outcomes = await Task.WhenAll(first, second);

        outcomes.Count(static outcome => outcome == ClientConnectionPromotionStatus.Promoted).Should().Be(1);
        outcomes.Count(static outcome => outcome == ClientConnectionPromotionStatus.AlreadyAuthenticated).Should().Be(1);
        sink.Connection.IdentityRevision.Should().Be(1);
    }

    [Fact]
    public async Task PromoteAsync_SecondPromotionAuthenticatedInitialAndDemotionShapes_AreRejected() {
        var ct = TestContext.Current.CancellationToken;
        await using var provider = BuildProvider();
        var registry = provider.GetRequiredService<IClientConnectionRegistry>();
        var anonymous = AnonymousSink("anonymous");
        var authenticated = Sink("authenticated", "user-1");
        await registry.RegisterAsync(anonymous, ct);
        await registry.RegisterAsync(authenticated, ct);

        (await registry.PromoteAsync("anonymous", AuthenticatedIdentity("user-1"), ct))
            .Should().Be(ClientConnectionPromotionStatus.Promoted);
        (await registry.PromoteAsync("anonymous", AuthenticatedIdentity("user-2"), ct))
            .Should().Be(ClientConnectionPromotionStatus.AlreadyAuthenticated);
        (await registry.PromoteAsync("authenticated", AuthenticatedIdentity("user-2"), ct))
            .Should().Be(ClientConnectionPromotionStatus.AlreadyAuthenticated);
        (await registry.PromoteAsync("anonymous", new ClientConnectionIdentity {
            Principal = new ClaimsPrincipal(new ClaimsIdentity()),
            PrincipalId = "user-3",
        }, ct)).Should().Be(ClientConnectionPromotionStatus.AlreadyAuthenticated);
    }

    [Fact]
    public async Task PromoteAsync_ClonesInputAndRejectsInvalidIdentityWithoutChangingSnapshot() {
        var ct = TestContext.Current.CancellationToken;
        await using var provider = BuildProvider();
        var registry = provider.GetRequiredService<IClientConnectionRegistry>();
        var inputIdentity = new ClaimsIdentity([new Claim(ClaimTypes.Name, "before")], "test");
        var inputMetadata = new Dictionary<string, string> { ["method"] = "token" };
        var sink = AnonymousSink("conn-1");
        await registry.RegisterAsync(sink, ct);
        var beforeInvalid = sink.Connection;

        var invalid = async () => await registry.PromoteAsync("conn-1", new ClientConnectionIdentity {
            Principal = new ClaimsPrincipal(new ClaimsIdentity()),
            PrincipalId = "user-1",
        }, ct);
        await invalid.Should().ThrowAsync<ArgumentException>();
        sink.Connection.Should().BeSameAs(beforeInvalid);

        (await registry.PromoteAsync("conn-1", new ClientConnectionIdentity {
            Principal = new ClaimsPrincipal(inputIdentity),
            PrincipalId = "user-1",
            Metadata = inputMetadata,
        }, ct)).Should().Be(ClientConnectionPromotionStatus.Promoted);
        inputIdentity.AddClaim(new Claim(ClaimTypes.Role, "administrator"));
        inputMetadata["method"] = "mutated";
        inputMetadata["new"] = "value";

        sink.Connection.Principal.IsInRole("administrator").Should().BeFalse();
        sink.Connection.Metadata.Should().ContainSingle().Which.Value.Should().Be("token");
    }

    [Fact]
    public async Task RegisterAsync_ClonesBootstrapContext() {
        var ct = TestContext.Current.CancellationToken;
        await using var provider = BuildProvider();
        var registry = provider.GetRequiredService<IClientConnectionRegistry>();
        var bootstrap = new byte[] { 1, 2, 3 };
        var identity = new ClaimsIdentity(authenticationType: "test") { BootstrapContext = bootstrap };
        var sink = new SimulatedClientConnection(
            principalId: "user-1", connectionId: "bootstrap", principal: new ClaimsPrincipal(identity));

        await registry.RegisterAsync(sink, ct);
        bootstrap[0] = 9;

        ((byte[])sink.Connection.Principal.Identities.Single().BootstrapContext!).Should().Equal(1, 2, 3);

        // A cyclic actor graph cannot be constructed with BCL ClaimsIdentity — its Actor setter rejects
        // circular references — so the registry's own cycle guard is defense-in-depth, and the reachable
        // bound is the actor depth limit covered by RegisterAsync_RejectsPrincipalGraphBeyondConfiguredLimits.
        var first = new ClaimsIdentity();
        var second = new ClaimsIdentity { Actor = first };
        var close = () => first.Actor = second;
        close.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public async Task PromoteAsync_MetadataBeyondConfiguredBound_LeavesSnapshotUnchanged() {
        var ct = TestContext.Current.CancellationToken;
        await using var provider = BuildProvider(options => options.MaxIdentityMetadataEntries = 0);
        var registry = provider.GetRequiredService<IClientConnectionRegistry>();
        var sink = AnonymousSink("conn-1");
        await registry.RegisterAsync(sink, ct);
        var before = sink.Connection;

        var promote = async () => await registry.PromoteAsync("conn-1", new ClientConnectionIdentity {
            Principal = new ClaimsPrincipal(new ClaimsIdentity(authenticationType: "test")),
            PrincipalId = "user-1",
            Metadata = new Dictionary<string, string> { ["method"] = "token" },
        }, ct);

        await promote.Should().ThrowAsync<ArgumentException>();
        sink.Connection.Should().BeSameAs(before);
    }

    [Fact]
    public async Task PromoteAsync_ObserverFailureDoesNotRollBackAndPeersStillRun() {
        var ct = TestContext.Current.CancellationToken;
        var recording = new PromotionRecordingObserver();
        await using var provider = BuildProvider(new PromotionThrowingObserver(), recording);
        var registry = provider.GetRequiredService<IClientConnectionRegistry>();
        var sink = AnonymousSink("conn-1");
        await registry.RegisterAsync(sink, ct);

        (await registry.PromoteAsync("conn-1", AuthenticatedIdentity("user-1"), ct))
            .Should().Be(ClientConnectionPromotionStatus.Promoted);

        sink.Connection.PrincipalId.Should().Be("user-1");
        recording.Promoted.Should().ContainSingle().Which.Current.Should().BeSameAs(sink.Connection);
    }

    [Fact]
    public async Task PromoteAsync_RacingUnregister_NotifiesPromotionBeforeDisconnect() {
        var ct = TestContext.Current.CancellationToken;
        var observer = new OrderedLifecycleObserver();
        await using var provider = BuildProvider(observer);
        var registry = provider.GetRequiredService<IClientConnectionRegistry>();
        var sink = AnonymousSink("conn-1");
        await registry.RegisterAsync(sink, ct);

        var promote = registry.PromoteAsync("conn-1", AuthenticatedIdentity("user-1"), ct).AsTask();
        await observer.PromotionEntered.Task.WaitAsync(ct);
        var unregister = registry.UnregisterAsync("conn-1", ct).AsTask();
        observer.AllowPromotion.SetResult();
        await Task.WhenAll(promote, unregister);

        observer.Edges.Should().Equal("promoted", "disconnected");
    }

    [Fact]
    public async Task UnregisterAsync_AfterPromotion_NotifiesWithFinalSnapshot() {
        var ct = TestContext.Current.CancellationToken;
        var observer = new RecordingObserver();
        await using var provider = BuildProvider(observer);
        var registry = provider.GetRequiredService<IClientConnectionRegistry>();
        var sink = AnonymousSink("conn-1");
        await registry.RegisterAsync(sink, ct);
        await registry.PromoteAsync("conn-1", AuthenticatedIdentity("user-1"), ct);

        await registry.UnregisterAsync("conn-1", ct);

        observer.Disconnected.Should().ContainSingle().Which.PrincipalId.Should().Be("user-1");
        observer.Disconnected.Single().IdentityRevision.Should().Be(1);
    }

    [Fact]
    public async Task RegisterAsync_RejectsPrincipalGraphBeyondConfiguredLimits() {
        var ct = TestContext.Current.CancellationToken;
        await using var provider = BuildProvider(options => {
            options.MaxPrincipalIdentities = 1;
            options.MaxPrincipalClaims = 1;
            options.MaxPrincipalActorDepth = 0;
        });
        var registry = provider.GetRequiredService<IClientConnectionRegistry>();
        var tooManyIdentities = new SimulatedClientConnection(
            principalId: "multi",
            connectionId: "multi",
            principal: new ClaimsPrincipal([
                new ClaimsIdentity(authenticationType: "first"),
                new ClaimsIdentity(authenticationType: "second"),
            ]));
        var tooManyClaims = new SimulatedClientConnection(
            principalId: "claims",
            connectionId: "claims",
            principal: new ClaimsPrincipal(new ClaimsIdentity([
                new Claim("a", "1"),
                new Claim("b", "2"),
            ], "test")));
        var actor = new ClaimsIdentity(authenticationType: "actor");
        var root = new ClaimsIdentity(authenticationType: "test") { Actor = actor };
        var tooDeep = new SimulatedClientConnection(
            principalId: "depth", connectionId: "depth", principal: new ClaimsPrincipal(root));

        Func<Task> registerIdentities = async () => await registry.RegisterAsync(tooManyIdentities, ct);
        Func<Task> registerClaims = async () => await registry.RegisterAsync(tooManyClaims, ct);
        Func<Task> registerDepth = async () => await registry.RegisterAsync(tooDeep, ct);
        await registerIdentities.Should().ThrowAsync<ArgumentException>().WithMessage("*identities*");
        await registerClaims.Should().ThrowAsync<ArgumentException>().WithMessage("*claims*");
        await registerDepth.Should().ThrowAsync<ArgumentException>().WithMessage("*depth*");
    }

    [Fact]
    public void AddElarionConnections_RejectsInvalidIdentityMetadataBounds() {
        var entries = () => new ServiceCollection().AddElarionConnections(options =>
            options.MaxIdentityMetadataEntries = -1);
        var keyLength = () => new ServiceCollection().AddElarionConnections(options =>
            options.MaxIdentityMetadataKeyLength = 0);
        var valueLength = () => new ServiceCollection().AddElarionConnections(options =>
            options.MaxIdentityMetadataValueLength = -1);
        var identities = () => new ServiceCollection().AddElarionConnections(options =>
            options.MaxPrincipalIdentities = 0);
        var claims = () => new ServiceCollection().AddElarionConnections(options =>
            options.MaxPrincipalClaims = -1);
        var actorDepth = () => new ServiceCollection().AddElarionConnections(options =>
            options.MaxPrincipalActorDepth = -1);

        entries.Should().Throw<ArgumentException>();
        keyLength.Should().Throw<ArgumentException>();
        valueLength.Should().Throw<ArgumentException>();
        identities.Should().Throw<ArgumentException>();
        claims.Should().Throw<ArgumentException>();
        actorDepth.Should().Throw<ArgumentException>();
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

    private static ServiceProvider BuildProvider(params IClientConnectionObserver[] observers) =>
        BuildProvider(configure: null, observers);

    private static ServiceProvider BuildProvider(Action<ElarionConnectionsOptions> configure) =>
        BuildProvider(configure, []);

    private static ServiceProvider BuildProvider(
        Action<ElarionConnectionsOptions>? configure,
        params IClientConnectionObserver[] observers) {
        var services = new ServiceCollection();
        services.AddElarionConnections(configure);
        foreach (var observer in observers) {
            services.AddSingleton(observer);
        }
        return services.BuildServiceProvider();
    }

    internal static SimulatedClientConnection Sink(string connectionId, string principalId) =>
        new(principalId: principalId, connectionId: connectionId);

    internal static SimulatedClientConnection AnonymousSink(string connectionId) =>
        new(connectionId: connectionId, principal: new ClaimsPrincipal(new ClaimsIdentity()));

    private static ClientConnectionIdentity AuthenticatedIdentity(string principalId) => new() {
        Principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, principalId)], authenticationType: "test")),
        PrincipalId = principalId,
    };

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

        public ValueTask OnIdentityPromotedAsync(
            ClientConnection previous,
            ClientConnection current,
            CancellationToken ct = default) =>
            ValueTask.CompletedTask;
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

        public ValueTask OnIdentityPromotedAsync(
            ClientConnection previous,
            ClientConnection current,
            CancellationToken ct = default) =>
            ValueTask.CompletedTask;
    }

    private sealed class ThrowingObserver : IClientConnectionObserver {
        public ValueTask OnConnectedAsync(IClientConnectionSink connection, CancellationToken ct = default) =>
            throw new InvalidOperationException("observer failure");

        public ValueTask OnDisconnectedAsync(ClientConnection connection, CancellationToken ct = default) =>
            throw new InvalidOperationException("observer failure");

        public ValueTask OnIdentityPromotedAsync(
            ClientConnection previous,
            ClientConnection current,
            CancellationToken ct = default) =>
            throw new InvalidOperationException("observer failure");
    }

    private sealed class PromotionThrowingObserver : IClientConnectionObserver {
        public ValueTask OnConnectedAsync(IClientConnectionSink connection, CancellationToken ct = default) =>
            ValueTask.CompletedTask;

        public ValueTask OnDisconnectedAsync(ClientConnection connection, CancellationToken ct = default) =>
            ValueTask.CompletedTask;

        public ValueTask OnIdentityPromotedAsync(
            ClientConnection previous,
            ClientConnection current,
            CancellationToken ct = default) =>
            throw new InvalidOperationException("observer failure");
    }

    private sealed class OrderedLifecycleObserver : IClientConnectionObserver {
        public TaskCompletionSource PromotionEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowPromotion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<string> Edges { get; } = [];

        public ValueTask OnConnectedAsync(IClientConnectionSink connection, CancellationToken ct = default) =>
            ValueTask.CompletedTask;

        public ValueTask OnIdentityPromotedAsync(
            ClientConnection previous,
            ClientConnection current,
            CancellationToken ct = default) =>
            new(ObservePromotionAsync());

        public ValueTask OnDisconnectedAsync(ClientConnection connection, CancellationToken ct = default) {
            Edges.Add("disconnected");
            return ValueTask.CompletedTask;
        }

        private async Task ObservePromotionAsync() {
            PromotionEntered.SetResult();
            await AllowPromotion.Task;
            Edges.Add("promoted");
        }
    }

    private sealed class PromotionRecordingObserver : IClientConnectionObserver {
        public List<(ClientConnection Previous, ClientConnection Current)> Promoted { get; } = [];

        public ValueTask OnConnectedAsync(IClientConnectionSink connection, CancellationToken ct = default) =>
            ValueTask.CompletedTask;

        public ValueTask OnDisconnectedAsync(ClientConnection connection, CancellationToken ct = default) =>
            ValueTask.CompletedTask;

        public ValueTask OnIdentityPromotedAsync(
            ClientConnection previous,
            ClientConnection current,
            CancellationToken ct = default) {
            Promoted.Add((previous, current));
            return ValueTask.CompletedTask;
        }
    }
}
