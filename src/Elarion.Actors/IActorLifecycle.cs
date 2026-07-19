namespace Elarion.Actors;

/// <summary>
/// Optional lifecycle hooks for an <c>[Actor]</c> class. <see cref="OnActivateAsync"/> runs before
/// the first message of an activation (load state here); <see cref="OnDeactivateAsync"/> runs after
/// the mailbox drains on passivation or shutdown (flush state here). Both run under the actor's
/// single-threaded guarantee.
/// </summary>
public interface IActorLifecycle {
    /// <summary>Called once per activation, before the first message is processed. An exception fails all queued calls and drops the activation.</summary>
    ValueTask OnActivateAsync(CancellationToken cancellationToken) {
        return ValueTask.CompletedTask;
    }

    /// <summary>Called once per activation after the mailbox has drained, on idle passivation or shutdown.</summary>
    ValueTask OnDeactivateAsync(CancellationToken cancellationToken) {
        return ValueTask.CompletedTask;
    }
}
