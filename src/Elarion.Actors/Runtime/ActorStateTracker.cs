namespace Elarion.Actors.Runtime;

/// <summary>
/// Per-activation-scope collection of the <see cref="IActorState{TState}"/> instances the activator
/// created, so the cell can load every snapshot after construction and before
/// <see cref="IActorLifecycle.OnActivateAsync"/> / the first message. A load failure fails the
/// activation (queued calls fail) — an actor never runs against half-loaded state.
/// </summary>
internal sealed class ActorStateTracker {
    private List<IActorStateSlot>? _slots;

    internal bool HasStates => _slots is { Count: > 0 };

    internal void Register(IActorStateSlot slot) => (_slots ??= []).Add(slot);

    internal async ValueTask LoadAllAsync(CancellationToken cancellationToken) {
        if (_slots is null) {
            return;
        }

        foreach (var slot in _slots) {
            await slot.LoadAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
