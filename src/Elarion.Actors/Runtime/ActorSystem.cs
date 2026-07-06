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
        ILoggerFactory loggerFactory) {
        var runtime = new ActorRuntime(
            scopeFactory, timeProvider, loggerFactory, new ActorCancellationPool(timeProvider));
        foreach (var registration in registrations) {
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
