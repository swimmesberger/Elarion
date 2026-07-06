namespace Elarion.Actors;

/// <summary>
/// Ambient information an actor instance can take as a constructor parameter.
/// </summary>
public interface IActorContext {
    /// <summary>The actor's logical name (telemetry/log identity).</summary>
    string ActorName { get; }

    /// <summary>
    /// Signaled when this activation is stopping (host shutdown after the grace period). Long-lived
    /// work inside a message should observe it in addition to the message's own token.
    /// </summary>
    CancellationToken Stopping { get; }
}

/// <summary>
/// The keyed actor context. Declaring a constructor parameter of this type is what makes an
/// <c>[Actor]</c> keyed: the runtime activates one instance per distinct <see cref="Key"/>.
/// </summary>
/// <typeparam name="TKey">The actor's key type.</typeparam>
public interface IActorContext<out TKey> : IActorContext where TKey : notnull {
    /// <summary>The key this activation was created for.</summary>
    TKey Key { get; }
}
