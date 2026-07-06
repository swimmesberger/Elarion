namespace Elarion.Actors;

/// <summary>
/// Marker implemented by generated facade interfaces of <em>singleton</em> actors (one activation
/// per process). Resolve with <see cref="IActorSystem.Get{TFacade}()"/>.
/// </summary>
public interface IActorFacade;

/// <summary>
/// Marker implemented by generated facade interfaces of <em>keyed</em> actors (one activation per
/// key, activated on first message). Resolve with the key-typed
/// <see cref="IActorSystem"/>.<c>Get</c> overloads.
/// </summary>
/// <typeparam name="TKey">The actor's key type.</typeparam>
public interface IActorFacade<TKey> where TKey : notnull;
