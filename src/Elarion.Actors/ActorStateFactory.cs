using Elarion.Abstractions.Serialization;
using Elarion.Actors.Runtime;
using Microsoft.Extensions.DependencyInjection;

namespace Elarion.Actors;

/// <summary>
/// Creates the <see cref="IActorState{TState}"/> a keyed or singleton actor declares as a
/// constructor parameter. Generated activators call this; a hand-rolled
/// <see cref="ActorRegistration{TActor,TKey,TFacade}.Activator"/> calls it the same way. The
/// returned state is registered for loading, which the runtime performs after construction and
/// before <see cref="IActorLifecycle.OnActivateAsync"/>.
/// </summary>
public static class ActorStateFactory {
    /// <summary>
    /// Creates the snapshot-backed state for the activation identified by <paramref name="context"/>.
    /// Must be called from inside an activator (the activation's DI scope); fails the activation
    /// loudly when no <see cref="IActorSnapshotStore"/> is registered.
    /// </summary>
    /// <typeparam name="TState">The persisted state type.</typeparam>
    /// <typeparam name="TKey">The actor's key type (<see cref="ActorSingletonKey"/> for singletons).</typeparam>
    public static IActorState<TState> Create<TState, TKey>(
        IServiceProvider serviceProvider,
        IActorContext<TKey> context)
        where TState : class
        where TKey : notnull {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        ArgumentNullException.ThrowIfNull(context);

        var store = serviceProvider.GetService<IActorSnapshotStore>()
                    ?? throw new InvalidOperationException(
                        $"Actor '{context.ActorName}' declares IActorState<{typeof(TState).Name}> but no "
                        + "IActorSnapshotStore is registered. Reference a snapshot store package and register it "
                        + "(e.g. Elarion.Actors.PostgreSql: services.AddElarionPostgreSqlActorSnapshots<TDbContext>()).");
        var serialization = serviceProvider.GetService<IElarionJsonSerialization>()
                            ?? throw new InvalidOperationException(
                                $"Actor '{context.ActorName}' declares IActorState<{typeof(TState).Name}> but Elarion's "
                                + "canonical JSON is not registered; call services.AddElarionJson().");

        var key = new ActorSnapshotKey(context.ActorName, context.Key.ToString() ?? string.Empty);
        var tracker = serviceProvider.GetRequiredService<ActorStateTracker>();
        var state = new ActorState<TState>(key, store, serialization, tracker);
        tracker.Register(state);
        return state;
    }
}
