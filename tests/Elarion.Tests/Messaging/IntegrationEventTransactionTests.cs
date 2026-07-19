using AwesomeAssertions;
using Elarion.Abstractions.Messaging;
using Elarion.Messaging.InMemory;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Testcontainers.PostgreSql;
using Xunit;

namespace Elarion.Tests.Messaging;

/// <summary>
/// End-to-end verification that the in-memory integration-event tier is commit-gated by the caller's EF Core
/// transaction: an event buffered while an explicit transaction is open is delivered only after the transaction
/// commits, and discarded when it rolls back. This exercises the real
/// <see cref="EventDispatchSaveChangesInterceptor"/> / <see cref="EventDispatchTransactionInterceptor"/> pair
/// against PostgreSQL (the EF in-memory provider does not raise transaction interceptors). Skips when Docker is
/// unavailable.
/// </summary>
[Trait("Category", "Integration")]
public sealed class IntegrationEventTransactionTests(IntegrationEventTransactionFixture fixture)
    : IClassFixture<IntegrationEventTransactionFixture> {
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task IntegrationEvent_DeliveredAfterCallerTransactionCommits() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        using var cts = new CancellationTokenSource(WaitTimeout);
        var recorder = new EventRecorder();
        await using var provider = BuildProvider(recorder);
        var pump = provider.GetServices<IHostedService>().Single();
        await pump.StartAsync(cts.Token);

        try {
            await using (var scope = provider.CreateAsyncScope()) {
                var db = scope.ServiceProvider.GetRequiredService<EventTestDbContext>();
                var bus = scope.ServiceProvider.GetRequiredService<IIntegrationEventBus>();
                await using var transaction = await db.Database.BeginTransactionAsync(cts.Token);
                db.Markers.Add(new Marker { Id = Guid.NewGuid() });
                await bus.PublishAsync(new SampleIntegrationEvent("committed"), cts.Token);
                await db.SaveChangesAsync(cts.Token);
                await transaction.CommitAsync(cts.Token);
            }

            await recorder.WaitForAsync(1, cts.Token);
            recorder.Items.Should().Equal("committed");
        }
        finally {
            await pump.StopAsync(cts.Token);
        }
    }

    [Fact]
    public async Task IntegrationEvent_DiscardedWhenCallerTransactionRollsBack() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        using var cts = new CancellationTokenSource(WaitTimeout);
        var recorder = new EventRecorder();
        await using var provider = BuildProvider(recorder);
        var pump = provider.GetServices<IHostedService>().Single();
        await pump.StartAsync(cts.Token);

        try {
            await using (var scope = provider.CreateAsyncScope()) {
                var db = scope.ServiceProvider.GetRequiredService<EventTestDbContext>();
                var bus = scope.ServiceProvider.GetRequiredService<IIntegrationEventBus>();
                await using var transaction = await db.Database.BeginTransactionAsync(cts.Token);
                db.Markers.Add(new Marker { Id = Guid.NewGuid() });
                await bus.PublishAsync(new SampleIntegrationEvent("rolledback"), cts.Token);
                await db.SaveChangesAsync(cts.Token);
                await transaction.RollbackAsync(cts.Token);
            }

            // The event was discarded on rollback, so no amount of pump time will deliver it.
            await Task.Delay(200, cts.Token);
            recorder.Items.Should().BeEmpty();
        }
        finally {
            await pump.StopAsync(cts.Token);
        }
    }

    [Fact]
    public async Task IntegrationEvent_RolledBackToSavepoint_IsNotDeliveredButPreSavepointEventIs() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        using var cts = new CancellationTokenSource(WaitTimeout);
        var recorder = new EventRecorder();
        await using var provider = BuildProvider(recorder);
        var pump = provider.GetServices<IHostedService>().Single();
        await pump.StartAsync(cts.Token);

        try {
            await using (var scope = provider.CreateAsyncScope()) {
                var db = scope.ServiceProvider.GetRequiredService<EventTestDbContext>();
                var bus = scope.ServiceProvider.GetRequiredService<IIntegrationEventBus>();
                await using var transaction = await db.Database.BeginTransactionAsync(cts.Token);

                // Publish before the savepoint: this event must survive the partial rollback.
                await bus.PublishAsync(new SampleIntegrationEvent("before-savepoint"), cts.Token);

                // A real EF Core savepoint must fire CreatedSavepoint on the dispatch interceptor.
                await transaction.CreateSavepointAsync("sp1", cts.Token);

                // Publish after the savepoint: this event is undone by the rollback-to-savepoint and must not
                // be delivered even though the outer transaction still commits (the idempotency-decorator shape).
                await bus.PublishAsync(new SampleIntegrationEvent("after-savepoint"), cts.Token);

                await transaction.RollbackToSavepointAsync("sp1", cts.Token);
                await transaction.CommitAsync(cts.Token);
            }

            await recorder.WaitForAsync(1, cts.Token);
            await Task.Delay(200, cts.Token);
            recorder.Items.Should().Equal("before-savepoint");
        }
        finally {
            await pump.StopAsync(cts.Token);
        }
    }

    private ServiceProvider BuildProvider(EventRecorder recorder) {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(recorder);
        services.AddSingleton(RecordingSubscriber());
        // The generic overload auto-attaches the commit-gating interceptors to EventTestDbContext via
        // IDbContextOptionsConfiguration, so a plain AddDbContext is all the host needs.
        services.AddElarionInMemoryEventBus<EventTestDbContext>();
        services.AddDbContext<EventTestDbContext>(options => options.UseNpgsql(fixture.ConnectionString));
        return services.BuildServiceProvider();
    }

    // Mirrors a generated integration-event consumer descriptor: a fan-out subscriber that records deliveries.
    private static EventSubscriptionDescriptor RecordingSubscriber() {
        return new EventSubscriptionDescriptor {
            EventType = typeof(SampleIntegrationEvent),
            Plane = EventPlane.Integration,
            ServiceType = typeof(EventRecorder),
            Order = 0,
            InvokeAsync = (sp, evt, _, _) => {
                sp.GetRequiredService<EventRecorder>().Add(((SampleIntegrationEvent)evt).Value);
                return ValueTask.CompletedTask;
            }
        };
    }

    private sealed record SampleIntegrationEvent(string Value) : IIntegrationEvent;

    private sealed class EventRecorder {
        private readonly object _gate = new();
        private readonly List<string> _items = [];
        private readonly SemaphoreSlim _signal = new(0);

        public IReadOnlyList<string> Items {
            get {
                lock (_gate) {
                    return _items.ToArray();
                }
            }
        }

        public void Add(string item) {
            lock (_gate) {
                _items.Add(item);
            }

            _signal.Release();
        }

        public async Task WaitForAsync(int count, CancellationToken ct) {
            for (var i = 0; i < count; i++) await _signal.WaitAsync(ct);
        }
    }
}

/// <summary>A trivial business entity so each test's transaction performs a real write alongside the event.</summary>
public sealed class Marker {
    public Guid Id { get; set; }
}

/// <summary>Integration context with a trivial table; the in-memory event interceptors attach via DI.</summary>
public sealed class EventTestDbContext(DbContextOptions<EventTestDbContext> options) : DbContext(options) {
    public DbSet<Marker> Markers => Set<Marker>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) {
        modelBuilder.Entity<Marker>(builder => {
            builder.ToTable("markers");
            builder.HasKey(marker => marker.Id);
        });
    }
}

/// <summary>Starts a disposable PostgreSQL container and creates the marker schema once.</summary>
public sealed class IntegrationEventTransactionFixture : IAsyncLifetime {
    private PostgreSqlContainer? _container;

    public bool IsAvailable { get; private set; }

    public string SkipReason { get; private set; } = "";

    public string ConnectionString { get; private set; } = "";

    public async ValueTask InitializeAsync() {
        PostgreSqlContainer container;
        try {
            container = new PostgreSqlBuilder("postgres:17-alpine").Build();
            await container.StartAsync();
        }
        catch (Exception ex) {
            SkipReason = $"PostgreSQL Testcontainer unavailable (Docker required): {ex.Message}";
            return;
        }

        _container = container;
        ConnectionString = container.GetConnectionString();
        await using var context = new EventTestDbContext(
            new DbContextOptionsBuilder<EventTestDbContext>().UseNpgsql(ConnectionString).Options);
        await context.Database.EnsureCreatedAsync();
        IsAvailable = true;
    }

    public async ValueTask DisposeAsync() {
        if (_container is not null) await _container.DisposeAsync();
    }
}
