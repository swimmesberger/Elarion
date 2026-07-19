using Elarion.Actors.Runtime;

namespace Elarion.Actors;

/// <summary>
/// A single actor's registration: identity, options, and the statically-typed factories the
/// generator emits (activator with explicit <c>GetRequiredService</c> calls, facade constructor).
/// Registered via <c>AddElarionActor</c>; application code normally never constructs one — the
/// per-module <c>Add{Module}Actors</c> extension does.
/// </summary>
public abstract class ActorRegistration {
    private protected ActorRegistration() {
    }

    /// <summary>The actor's logical name (telemetry/log identity).</summary>
    public required string Name { get; init; }

    /// <summary>The actor's runtime options.</summary>
    public required ActorOptions Options { get; init; }

    internal abstract Type FacadeType { get; }

    internal abstract IActorHostEntry CreateHost(ActorRuntime runtime);
}

/// <summary>
/// The typed actor registration. <typeparamref name="TKey"/> is
/// <see cref="ActorSingletonKey"/> for singleton actors.
/// </summary>
/// <typeparam name="TActor">The actor implementation type.</typeparam>
/// <typeparam name="TKey">The actor key type.</typeparam>
/// <typeparam name="TFacade">The generated facade interface.</typeparam>
public sealed class ActorRegistration<TActor, TKey, TFacade> : ActorRegistration
    where TActor : class
    where TKey : notnull
    where TFacade : class {
    /// <summary>
    /// Creates the actor instance for an activation. Receives the activation's DI scope provider
    /// and the actor context carrying the key.
    /// </summary>
    public required Func<IServiceProvider, IActorContext<TKey>, TActor> Activator { get; init; }

    /// <summary>Creates the facade over an invocation handle (the key is bound inside the handle).</summary>
    public required Func<ActorHandle<TActor>, TFacade> Facade { get; init; }

    internal override Type FacadeType => typeof(TFacade);

    internal override IActorHostEntry CreateHost(ActorRuntime runtime) {
        return new ActorHost<TActor, TKey, TFacade>(this, runtime);
    }
}
