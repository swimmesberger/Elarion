namespace Elarion.Actors.Runtime;

/// <summary>
/// Routes a work item to the live activation for a key, creating one when absent. Implemented by
/// the actor host; the key is boxed once per facade, unboxed once per call.
/// </summary>
internal interface IActorMailboxRouter<TActor> where TActor : class {
    ValueTask EnqueueAsync(object key, ActorWorkItem<TActor> item, CancellationToken cancellationToken);
}

/// <summary>A registered actor's host as seen by the actor system: facade factory + shutdown.</summary>
internal interface IActorHostEntry {
    string Name { get; }

    Type FacadeType { get; }

    object CreateFacade(object key);

    Task StopAsync(CancellationToken cancellationToken);
}
