namespace Elarion.Actors.Runtime;

/// <summary>The runtime <see cref="IActorContext{TKey}"/> handed to an activation's constructor.</summary>
internal sealed class ActorContext<TKey>(string actorName, TKey key, CancellationToken stopping)
    : IActorContext<TKey> where TKey : notnull {
    public string ActorName { get; } = actorName;

    public TKey Key { get; } = key;

    public CancellationToken Stopping { get; } = stopping;
}
