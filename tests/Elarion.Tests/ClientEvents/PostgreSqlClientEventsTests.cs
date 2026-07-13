using System.Text.Json.Serialization;
using AwesomeAssertions;
using Elarion.Abstractions.ClientEvents;
using Elarion.Abstractions.Serialization;
using Elarion.ClientEvents;
using Elarion.ClientEvents.PostgreSql;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Elarion.Tests.ClientEvents;

/// <summary>
/// Starts a disposable PostgreSQL container for the client-event <c>LISTEN/NOTIFY</c> integration tests.
/// No schema is needed — the broadcaster rides notifications only. When Docker is not available the fixture
/// records a skip reason instead of failing, so the suite still runs (and these tests skip) without Docker.
/// </summary>
public sealed class PostgreSqlClientEventsFixture : IAsyncLifetime {
    private PostgreSqlContainer? _container;

    /// <summary>Gets a value indicating whether the container started.</summary>
    public bool IsAvailable { get; private set; }

    /// <summary>Gets the reason the integration tests are skipped when <see cref="IsAvailable"/> is false.</summary>
    public string SkipReason { get; private set; } = "";

    /// <summary>Gets the container connection string.</summary>
    public string ConnectionString { get; private set; } = "";

    public async ValueTask InitializeAsync() {
        PostgreSqlContainer container;
        try {
            // Build() validates the Docker endpoint, so it must run inside the guard too.
            container = new PostgreSqlBuilder("postgres:17-alpine").Build();
            await container.StartAsync();
        }
        catch (Exception ex) {
            // The only expected failure here is Docker being unavailable; surface it as a skip.
            SkipReason = $"PostgreSQL Testcontainer unavailable (Docker required): {ex.Message}";
            return;
        }

        _container = container;
        ConnectionString = container.GetConnectionString();
        IsAvailable = true;
    }

    public async ValueTask DisposeAsync() {
        if (_container is not null) {
            await _container.DisposeAsync();
        }
    }
}

/// <summary>
/// Cross-node delivery tests for the PostgreSQL <c>LISTEN/NOTIFY</c> client-event broadcaster: two
/// independent runtime+listener pairs over one database — the multi-node topology — where a publish on one
/// node must reach the other node's subscribers (and the publishing node's own, through the same loop-back
/// path). Skips when Docker is unavailable.
/// </summary>
[Trait("Category", "Integration")]
public sealed partial class PostgreSqlClientEventsIntegrationTests(PostgreSqlClientEventsFixture fixture)
    : IClassFixture<PostgreSqlClientEventsFixture> {
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly TimeSpan DeliveryTimeout = TimeSpan.FromSeconds(15);

    // Long enough for a wrongly-sent notification to arrive, short enough to keep the suite fast.
    private static readonly TimeSpan QuietWindow = TimeSpan.FromMilliseconds(750);

    private sealed record InvoiceChanged : IClientEvent {
        public required Guid InvoiceId { get; init; }
    }

    private sealed record NoisyChanged : IClientEvent {
        public required string Blob { get; init; }
    }

    [JsonSerializable(typeof(InvoiceChanged))]
    [JsonSerializable(typeof(NoisyChanged))]
    private sealed partial class PostgreSqlClientEventTestContext : JsonSerializerContext;

    private static ClientEventSubscription Subscription(string topic, ClientEventScope scope) =>
        new() { Topic = topic, Scope = scope };

    [Fact]
    public async Task PublishOnOneNode_ReachesSubscribersOnEveryNode() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        await using var nodeA = await EventNode.StartAsync(fixture.ConnectionString, Ct);
        await using var nodeB = await EventNode.StartAsync(fixture.ConnectionString, Ct);
        using var remote = nodeB.Source.Subscribe([Subscription("test.invoiceChanged", ClientEventScope.Global)]);
        using var local = nodeA.Source.Subscribe([Subscription("test.invoiceChanged", ClientEventScope.Global)]);

        var invoiceId = Guid.CreateVersion7();
        await nodeA.Publisher.PublishAsync(new InvoiceChanged { InvoiceId = invoiceId }, ClientEventScope.Global, Ct);

        var onB = await remote.Events.ReadAsync(Ct).AsTask().WaitAsync(DeliveryTimeout, Ct);
        // The publishing node observes its own event through the same loop-back path.
        var onA = await local.Events.ReadAsync(Ct).AsTask().WaitAsync(DeliveryTimeout, Ct);

        onB.Topic.Should().Be("test.invoiceChanged");
        onB.Payload.Should().Contain(invoiceId.ToString());
        onA.Id.Should().Be(onB.Id);
        onA.Payload.Should().Be(onB.Payload);
    }

    [Fact]
    public async Task UserScope_SurvivesTheWire_AndStaysInvisibleToOtherUsers() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        await using var nodeA = await EventNode.StartAsync(fixture.ConnectionString, Ct);
        await using var nodeB = await EventNode.StartAsync(fixture.ConnectionString, Ct);
        using var alice = nodeB.Source.Subscribe([Subscription("test.invoiceChanged", ClientEventScope.User("alice"))]);
        using var bob = nodeB.Source.Subscribe([Subscription("test.invoiceChanged", ClientEventScope.User("bob"))]);

        await nodeA.Publisher.PublishAsync(
            new InvoiceChanged { InvoiceId = Guid.CreateVersion7() }, ClientEventScope.User("alice"), Ct);

        var delivered = await alice.Events.ReadAsync(Ct).AsTask().WaitAsync(DeliveryTimeout, Ct);
        delivered.Scope.Should().Be(ClientEventScope.User("alice"));

        await Task.Delay(QuietWindow, Ct);
        bob.Events.TryRead(out _).Should().BeFalse();
    }

    [Fact]
    public async Task MalformedNotification_IsIgnored_AndTheStreamStaysLive() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        await using var node = await EventNode.StartAsync(fixture.ConnectionString, Ct);
        using var handle = node.Source.Subscribe([Subscription("test.invoiceChanged", ClientEventScope.Global)]);

        await using (var command = node.Broadcaster.DataSource.CreateCommand("SELECT pg_notify($1, $2)")) {
            command.Parameters.AddWithValue(new PostgreSqlClientEventOptions().ChannelName);
            command.Parameters.AddWithValue("not json");
            await command.ExecuteNonQueryAsync(Ct);
        }

        await Task.Delay(QuietWindow, Ct);
        handle.Events.TryRead(out _).Should().BeFalse();

        await node.Publisher.PublishAsync(
            new InvoiceChanged { InvoiceId = Guid.CreateVersion7() }, ClientEventScope.Global, Ct);
        var delivered = await handle.Events.ReadAsync(Ct).AsTask().WaitAsync(DeliveryTimeout, Ct);
        delivered.Topic.Should().Be("test.invoiceChanged");
    }

    [Fact]
    public async Task OversizedPayload_IsDroppedEverywhere_InsteadOfDivergingAcrossNodes() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        await using var node = await EventNode.StartAsync(fixture.ConnectionString, Ct);
        using var handle = node.Source.Subscribe([Subscription("test.noisyChanged", ClientEventScope.Global)]);

        await node.Publisher.PublishAsync(
            new NoisyChanged { Blob = new string('x', 9_000) }, ClientEventScope.Global, Ct);

        await Task.Delay(QuietWindow, Ct);
        handle.Events.TryRead(out _).Should().BeFalse();
    }

    [Fact]
    public async Task FirstListenEstablishment_DeliversTheConnectedHint() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        // Subscribe BEFORE the listener starts: notifications sent in that window are lost (SSE serves
        // immediately; the first connect attempt can back off), so the first establishment itself must tell
        // subscribers to re-query — not only reconnects.
        var services = new ServiceCollection();
        services.AddLogging();
        services.ConfigureElarionJson(o => o.TypeInfoResolvers.Add(PostgreSqlClientEventTestContext.Default));
        services.AddElarionClientEvents(events => events.AddTopic<InvoiceChanged>("test.invoiceChanged"));
        services.AddElarionPostgreSqlClientEvents(fixture.ConnectionString);
        await using var provider = services.BuildServiceProvider();

        var source = provider.GetRequiredService<IClientEventSubscriptionSource>();
        using var handle = source.Subscribe([Subscription("test.invoiceChanged", ClientEventScope.Global)]);

        var listener = provider.GetServices<IHostedService>().OfType<PostgreSqlClientEventListener>().Single();
        await listener.StartAsync(Ct);
        try {
            await listener.Listening.WaitAsync(TimeSpan.FromSeconds(30), Ct);

            var control = await handle.Events.ReadAsync(Ct).AsTask().WaitAsync(DeliveryTimeout, Ct);
            control.Topic.Should().Be(ClientEventControlEvents.Connected);
        }
        finally {
            await listener.StopAsync(CancellationToken.None);
            listener.Dispose();
        }
    }

    [Fact]
    public async Task TerminatedListenConnection_ReconnectsDeliversTheConnectedHint_AndStaysLive() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        await using var node = await EventNode.StartAsync(fixture.ConnectionString, Ct);
        using var handle = node.Source.Subscribe([Subscription("test.invoiceChanged", ClientEventScope.Global)]);

        await TerminateListenBackendsAsync(fixture.ConnectionString, Ct);

        // The re-established LISTEN makes every local subscriber re-query (events may have been missed) …
        var control = await handle.Events.ReadAsync(Ct).AsTask().WaitAsync(DeliveryTimeout, Ct);
        control.Topic.Should().Be(ClientEventControlEvents.Connected);

        // … and the stream is live again end-to-end.
        var invoiceId = Guid.CreateVersion7();
        await node.Publisher.PublishAsync(new InvoiceChanged { InvoiceId = invoiceId }, ClientEventScope.Global, Ct);
        var delivered = await handle.Events.ReadAsync(Ct).AsTask().WaitAsync(DeliveryTimeout, Ct);
        delivered.Topic.Should().Be("test.invoiceChanged");
        delivered.Payload.Should().Contain(invoiceId.ToString());
    }

    /// <summary>Kills the LISTEN backend(s) server-side, simulating a dropped listen connection.</summary>
    private static async Task TerminateListenBackendsAsync(string connectionString, CancellationToken ct) {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT pg_terminate_backend(pid) FROM pg_stat_activity " +
            "WHERE pid <> pg_backend_pid() AND query ILIKE 'LISTEN%'";
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>One "node": a full client-event runtime with its running listener over its own data source.</summary>
    private sealed class EventNode : IAsyncDisposable {
        private readonly ServiceProvider _provider;
        private readonly PostgreSqlClientEventListener _listener;

        private EventNode(ServiceProvider provider, PostgreSqlClientEventListener listener) {
            _provider = provider;
            _listener = listener;
            Source = provider.GetRequiredService<IClientEventSubscriptionSource>();
            Publisher = provider.GetRequiredService<IClientEventPublisher>();
            Broadcaster = provider.GetRequiredService<PostgreSqlClientEventBroadcaster>();
        }

        public IClientEventSubscriptionSource Source { get; }

        public IClientEventPublisher Publisher { get; }

        public PostgreSqlClientEventBroadcaster Broadcaster { get; }

        public static async Task<EventNode> StartAsync(string connectionString, CancellationToken cancellationToken) {
            var services = new ServiceCollection();
            services.AddLogging();
            services.ConfigureElarionJson(o => o.TypeInfoResolvers.Add(PostgreSqlClientEventTestContext.Default));
            services.AddElarionClientEvents(events => events
                .AddTopic<InvoiceChanged>("test.invoiceChanged")
                .AddTopic<NoisyChanged>("test.noisyChanged"));
            services.AddElarionPostgreSqlClientEvents(connectionString);

            var provider = services.BuildServiceProvider();
            var listener = provider.GetServices<IHostedService>().OfType<PostgreSqlClientEventListener>().Single();
            await listener.StartAsync(cancellationToken);
            await listener.Listening.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            return new EventNode(provider, listener);
        }

        public async ValueTask DisposeAsync() {
            await _listener.StopAsync(CancellationToken.None);
            _listener.Dispose();
            await _provider.DisposeAsync();
        }
    }
}

/// <summary>Registration-shape tests for <c>AddElarionPostgreSqlClientEvents</c>; no database needed.</summary>
public sealed class PostgreSqlClientEventsRegistrationTests {
    private const string ConnectionString = "Host=localhost;Database=elarion;Username=elarion;Password=elarion";

    [Fact]
    public void ReplacesInProcessBroadcasterAndRegistersListener() {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddElarionClientEvents();
        services.AddElarionPostgreSqlClientEvents(ConnectionString);

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IClientEventBroadcaster>().Should().BeOfType<PostgreSqlClientEventBroadcaster>();
        provider.GetServices<IHostedService>()
            .Should().ContainSingle(service => service is PostgreSqlClientEventListener);
    }

    [Fact]
    public void IsAuthoritativeRegardlessOfRegistrationOrder() {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddElarionPostgreSqlClientEvents(ConnectionString);
        services.AddElarionClientEvents();

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IClientEventBroadcaster>().Should().BeOfType<PostgreSqlClientEventBroadcaster>();
    }
}
