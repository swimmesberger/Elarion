namespace Elarion.Abstractions;

/// <summary>
/// A typed, in-process "send": routes a request to its handler <b>by type</b> — resolving
/// <see cref="IHandler{TRequest, TResponse}"/> with a <see cref="Result{T}"/> response from the
/// <b>ambient</b> DI scope and invoking it through the full decorator pipeline. The mediator-style entry
/// point for calling one handler from another without injecting that handler's interface directly, and the
/// typed replacement for the former <c>IDomainEventBus.RequestAsync</c> (see ADR-0010).
/// </summary>
/// <remarks>
/// Because it resolves from the ambient scope, the call runs in the caller's transaction. For a fresh seeded
/// scope (a custom transport or background job) use <c>HandlerInvoker</c> instead; for name-routed transport
/// dispatch use <c>HandlerDispatcher</c>. Prefer injecting the specific <see cref="IHandler{TRequest, TResponse}"/>
/// directly when you call only one — this is the convenience for code that dispatches several by type.
/// </remarks>
/// <example>
/// <code>
/// sealed class PlaceOrder(IHandlerSender sender) : IHandler&lt;PlaceOrder.Command, Result&lt;OrderId&gt;&gt; {
///     public async ValueTask&lt;Result&lt;OrderId&gt;&gt; HandleAsync(Command command, CancellationToken ct) {
///         var quote = await sender.SendAsync&lt;PriceQuote, Money&gt;(new PriceQuote(command.Sku), ct); // runs in this transaction
///         ...
///     }
/// }
/// </code>
/// </example>
public interface IHandlerSender {
    /// <summary>
    /// Resolves <see cref="IHandler{TRequest, TResponse}"/> (with a <c>Result&lt;TResponse&gt;</c> response)
    /// from the ambient scope and invokes it, returning its <see cref="Result{T}"/>.
    /// </summary>
    /// <typeparam name="TRequest">The request type.</typeparam>
    /// <typeparam name="TResponse">The success value type.</typeparam>
    ValueTask<Result<TResponse>> SendAsync<TRequest, TResponse>(TRequest request, CancellationToken ct = default)
        where TRequest : notnull;
}
