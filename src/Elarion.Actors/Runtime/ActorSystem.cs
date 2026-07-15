using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Elarion.Actors.Runtime;

/// <summary>
/// The default <see cref="IActorSystem"/>: a facade-type → host map built once from the DI-collected
/// <see cref="ActorRegistration"/>s.
/// </summary>
internal sealed class ActorSystem : IActorSystem {
    private readonly Dictionary<Type, IActorHostEntry> _hostsByFacade = [];

    public ActorSystem(
        IEnumerable<ActorRegistration> registrations,
        IServiceScopeFactory scopeFactory,
        TimeProvider timeProvider,
        ILoggerFactory loggerFactory,
        IActorHomeLease? homeLease = null,
        IActorPlacementResolver? placementResolver = null) {
        var runtime = new ActorRuntime(
            scopeFactory,
            timeProvider,
            loggerFactory,
            new ActorCancellationPool(timeProvider),
            homeLease,
            placementResolver);
        foreach (var registration in registrations) {
            if (registration.Options.Placement == ActorPlacementMode.SingleHome && homeLease is null) {
                // Declared intent without enforcement (single-instance / local dev) is legal but
                // worth one loud line: on a multi-instance deployment this is the misconfiguration.
                loggerFactory.CreateLogger("Elarion.Actors." + registration.Name).LogWarning(
                    "Actor {Actor} is configured for SingleHome placement but no IActorHomeLease is registered; "
                    + "single-homing is not enforced on this instance.",
                    registration.Name);
            }

            if (registration.Options.Placement == ActorPlacementMode.VirtualShards &&
                placementResolver is null) {
                loggerFactory.CreateLogger("Elarion.Actors." + registration.Name).LogWarning(
                    "Actor {Actor} is configured for virtual-shard placement but no "
                    + "IActorPlacementResolver is registered; placement is not enforced on this instance.",
                    registration.Name);
            }

            var host = registration.CreateHost(runtime);
            if (!_hostsByFacade.TryAdd(host.FacadeType, host)) {
                throw new InvalidOperationException(
                    $"Duplicate actor registration for facade '{host.FacadeType}' (actor '{registration.Name}').");
            }
        }
    }

    public TFacade Get<TFacade>() where TFacade : class, IActorFacade =>
        CreateFacade<TFacade>(ActorSingletonKey.Value);

    public TFacade Get<TFacade>(string key) where TFacade : class, IActorFacade<string> =>
        CreateFacade<TFacade>(key);

    public TFacade Get<TFacade>(Guid key) where TFacade : class, IActorFacade<Guid> =>
        CreateFacade<TFacade>(key);

    public TFacade Get<TFacade>(long key) where TFacade : class, IActorFacade<long> =>
        CreateFacade<TFacade>(key);

    public TFacade Get<TFacade>(int key) where TFacade : class, IActorFacade<int> =>
        CreateFacade<TFacade>(key);

    public TFacade GetByKey<TFacade, TKey>(TKey key)
        where TFacade : class, IActorFacade<TKey>
        where TKey : notnull =>
        CreateFacade<TFacade>(key);

    internal Task StopAsync(CancellationToken cancellationToken) =>
        Task.WhenAll(_hostsByFacade.Values.Select(host => host.StopAsync(cancellationToken)));

    private TFacade CreateFacade<TFacade>(object key) where TFacade : class {
        if (!_hostsByFacade.TryGetValue(typeof(TFacade), out var host)) {
            throw new InvalidOperationException(
                $"No actor is registered for facade '{typeof(TFacade)}'. Check that the owning module is " +
                "enabled and its generated actor registration (Add{Module}Actors) ran.");
        }

        return (TFacade)host.CreateFacade(key);
    }
}

/// <summary>
/// Flushes the actor system on host shutdown: drains every mailbox and runs
/// <see cref="IActorLifecycle.OnDeactivateAsync"/> per live activation.
/// </summary>
internal sealed class ActorSystemLifecycleHost(ActorSystem system) : IHostedService {
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => system.StopAsync(cancellationToken);
}
