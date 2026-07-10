using AwesomeAssertions;
using Elarion.Actors;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Elarion.Tests.Actors;

/// <summary>
/// The ADR-0048 single-homing gate: <c>SingleHomed</c> actors run only while the registered
/// <see cref="IActorHomeLease"/> is held; without a registered lease the declaration is unenforced.
/// </summary>
public sealed class ActorHomeTests {
    private static CancellationToken TestToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SingleHomedActor_OnTheHome_Runs() {
        var lease = new FakeHomeLease { IsHeld = true };
        await using var provider = CreateProvider(lease);
        var pinger = provider.GetRequiredService<IActorSystem>().Get<IPinger>("a");

        (await pinger.Ping(TestToken)).Should().Be(1);
    }

    [Fact]
    public async Task SingleHomedActor_NotOnTheHome_FailsWithThePointer() {
        var lease = new FakeHomeLease { IsHeld = false, CurrentHolder = "web-2:abc" };
        await using var provider = CreateProvider(lease);
        var pinger = provider.GetRequiredService<IActorSystem>().Get<IPinger>("a");

        var act = async () => await pinger.Ping(TestToken);
        var exception = (await act.Should().ThrowAsync<ActorNotHomedException>()).Which;
        exception.ActorName.Should().Be("Pinger");
        exception.CurrentHolder.Should().Be("web-2:abc");
    }

    [Fact]
    public async Task LeaseLoss_StopsNewCalls_RegainAllowsThemAgain() {
        var lease = new FakeHomeLease { IsHeld = true };
        await using var provider = CreateProvider(lease);
        var pinger = provider.GetRequiredService<IActorSystem>().Get<IPinger>("a");
        await pinger.Ping(TestToken);

        lease.IsHeld = false;
        var act = async () => await pinger.Ping(TestToken);
        await act.Should().ThrowAsync<ActorNotHomedException>();

        lease.IsHeld = true;
        (await pinger.Ping(TestToken)).Should().Be(2);
    }

    [Fact]
    public async Task NonSingleHomedActor_IgnoresTheLease() {
        var lease = new FakeHomeLease { IsHeld = false };
        await using var provider = CreateProvider(lease);
        var free = provider.GetRequiredService<IActorSystem>().Get<IFreeRunner>("a");

        (await free.Ping(TestToken)).Should().Be(1);
    }

    [Fact]
    public async Task AddElarionActorHome_AdaptsTheKeyedRoleLease() {
        // ADR-0049: the actor home is a view over a keyed IRoleLease — the generic leader-election
        // primitive; this binding is what AddElarionPostgreSqlActorHome composes.
        var roleLease = new FakeRoleLease { Role = "actors", IsHeld = false, CurrentHolder = "web-9" };
        var services = new ServiceCollection();
        services.AddKeyedSingleton<Elarion.Abstractions.Coordination.IRoleLease>("actors", roleLease);
        services.AddElarionActorHome();
        services.AddElarionActorSystem();
        AddPingerActor(services, singleHomed: true);
        await using var provider = services.BuildServiceProvider();
        var pinger = provider.GetRequiredService<IActorSystem>().Get<IPinger>("a");

        var act = async () => await pinger.Ping(TestToken);
        (await act.Should().ThrowAsync<ActorNotHomedException>()).Which.CurrentHolder.Should().Be("web-9");

        roleLease.IsHeld = true;
        (await pinger.Ping(TestToken)).Should().Be(1);
    }

    [Fact]
    public async Task NoLeaseRegistered_SingleHomedIsUnenforced() {
        var services = new ServiceCollection();
        services.AddElarionActorSystem();
        AddPingerActor(services, singleHomed: true);
        await using var provider = services.BuildServiceProvider();
        var pinger = provider.GetRequiredService<IActorSystem>().Get<IPinger>("a");

        (await pinger.Ping(TestToken)).Should().Be(1);
    }

    private static ServiceProvider CreateProvider(FakeHomeLease lease) {
        var services = new ServiceCollection();
        services.AddSingleton<IActorHomeLease>(lease);
        services.AddElarionActorSystem();
        AddPingerActor(services, singleHomed: true);
        services.AddElarionActor(new ActorRegistration<PingerActor, string, IFreeRunner> {
            Name = "FreeRunner",
            Options = new ActorOptions(),
            Activator = static (_, _) => new PingerActor(),
            Facade = static handle => new FreeRunnerFacade(handle)
        });
        return services.BuildServiceProvider();
    }

    private static void AddPingerActor(IServiceCollection services, bool singleHomed) =>
        services.AddElarionActor(new ActorRegistration<PingerActor, string, IPinger> {
            Name = "Pinger",
            Options = new ActorOptions { SingleHomed = singleHomed },
            Activator = static (_, _) => new PingerActor(),
            Facade = static handle => new PingerFacade(handle)
        });

    private sealed class FakeHomeLease : IActorHomeLease {
        public bool IsHeld { get; set; }

        public string? CurrentHolder { get; set; }
    }

    private sealed class FakeRoleLease : Elarion.Abstractions.Coordination.IRoleLease {
        public required string Role { get; init; }

        public bool IsHeld { get; set; }

        public string? CurrentHolder { get; set; }
    }

    public interface IPinger : IActorFacade<string> {
        ValueTask<int> Ping(CancellationToken cancellationToken = default);
    }

    public interface IFreeRunner : IActorFacade<string> {
        ValueTask<int> Ping(CancellationToken cancellationToken = default);
    }

    public sealed class PingerActor {
        private int _count;

        public Task<int> Ping() => Task.FromResult(++_count);
    }

    private sealed class PingerFacade(ActorHandle<PingerActor> handle) : IPinger {
        public ValueTask<int> Ping(CancellationToken cancellationToken = default) =>
            handle.InvokeAsync(new PingItem(), cancellationToken);
    }

    private sealed class FreeRunnerFacade(ActorHandle<PingerActor> handle) : IFreeRunner {
        public ValueTask<int> Ping(CancellationToken cancellationToken = default) =>
            handle.InvokeAsync(new PingItem(), cancellationToken);
    }

    private sealed class PingItem : ActorWorkItem<PingerActor, int> {
        public override string MethodName => "Ping";

        protected override async ValueTask<int> InvokeAsync(PingerActor actor, CancellationToken cancellationToken) =>
            await actor.Ping().ConfigureAwait(false);
    }
}
