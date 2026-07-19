namespace Elarion.Actors;

/// <summary>
/// On a keyed actor's <c>[ConsumeEvent]</c> method, names the event property that supplies the actor
/// key — the activation the relayed event is routed to. Only needed to disambiguate: when the event
/// has exactly one property assignable to the actor's key type the generator infers it, and a
/// singleton actor needs no key at all.
/// </summary>
/// <remarks>
/// This lives in <c>Elarion.Actors</c>, not on <c>[ConsumeEvent]</c> (which stays an actor-agnostic
/// contract in <c>Elarion.Abstractions</c>): expressing an actor key is an actor concern. The named
/// property must resolve to a property on the consumed event whose type is assignable to the actor's
/// key type, otherwise <c>ELACT008</c> is reported.
/// </remarks>
/// <example>
/// <code>
/// [Actor]
/// public sealed class OrderFulfillmentActor(IActorContext&lt;Guid&gt; context) {
///     // OrderPlaced carries two Guids (OrderId, CustomerId) — inference is ambiguous, so name one:
///     [ConsumeEvent, ActorKey(nameof(OrderPlaced.OrderId))]
///     public Task OnPlaced(OrderPlaced e) => /* ... */;
/// }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Method, Inherited = false, AllowMultiple = false)]
public sealed class ActorKeyAttribute : Attribute {
    /// <summary>Initializes the attribute with the event property that supplies the actor key.</summary>
    /// <param name="propertyName">
    /// The name of a property on the consumed event whose type is assignable to the actor's key type.
    /// Use <c>nameof</c> so a rename stays a compile error.
    /// </param>
    public ActorKeyAttribute(string propertyName) {
        PropertyName = propertyName;
    }

    /// <summary>The event property that supplies the actor key.</summary>
    public string PropertyName { get; }
}
