namespace Elarion.Actors;

/// <summary>
/// Resolves typed actor facades. Keyed actors are virtual: <c>Get</c> never activates anything —
/// the activation is created lazily by the first call and passivated after its idle timeout — so a
/// facade is a cheap, always-valid address, not a live handle.
/// </summary>
/// <remarks>
/// The common key types have dedicated overloads so call sites read
/// <c>actors.Get&lt;IOrderFulfillment&gt;(orderId)</c>; any other <c>notnull</c> key works through
/// <see cref="GetByKey{TFacade, TKey}"/>.
/// </remarks>
public interface IActorSystem {
    /// <summary>Resolves the facade of a singleton actor.</summary>
    TFacade Get<TFacade>() where TFacade : class, IActorFacade;

    /// <summary>Resolves the facade of a <see cref="string"/>-keyed actor for <paramref name="key"/>.</summary>
    TFacade Get<TFacade>(string key) where TFacade : class, IActorFacade<string>;

    /// <summary>Resolves the facade of a <see cref="Guid"/>-keyed actor for <paramref name="key"/>.</summary>
    TFacade Get<TFacade>(Guid key) where TFacade : class, IActorFacade<Guid>;

    /// <summary>Resolves the facade of a <see cref="long"/>-keyed actor for <paramref name="key"/>.</summary>
    TFacade Get<TFacade>(long key) where TFacade : class, IActorFacade<long>;

    /// <summary>Resolves the facade of an <see cref="int"/>-keyed actor for <paramref name="key"/>.</summary>
    TFacade Get<TFacade>(int key) where TFacade : class, IActorFacade<int>;

    /// <summary>Resolves the facade of a keyed actor with an arbitrary key type.</summary>
    TFacade GetByKey<TFacade, TKey>(TKey key)
        where TFacade : class, IActorFacade<TKey>
        where TKey : notnull;
}
