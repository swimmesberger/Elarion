namespace Elarion.Abstractions;

/// <summary>
/// Defines a handler that processes a request and returns a response.
/// Handlers are the primary unit of business logic in the application.
/// </summary>
/// <typeparam name="TRequest">The request type.</typeparam>
/// <typeparam name="TResponse">The response type (typically <see cref="Result{T}"/>).</typeparam>
public interface IHandler<in TRequest, TResponse> {
    /// <summary>Handles the request and returns a response.</summary>
    ValueTask<TResponse> HandleAsync(TRequest request, CancellationToken ct);
}

/// <summary>
/// A convenience handler for business logic that produces no response. It is sugar for
/// <see cref="IHandler{TRequest, TResponse}"/> of <typeparamref name="T"/> and
/// <see cref="Result{T}"/> of <see cref="Unit"/>: implementers return the non-generic
/// <see cref="Result"/>, and a default interface method adapts it to
/// <c>Result&lt;Unit&gt;</c>. Because the two-argument interface is inherited, the handler
/// source generator discovers, registers, and decorates the type with no special-casing.
/// </summary>
/// <typeparam name="T">The request type (for event handlers, the event itself).</typeparam>
/// <example>
/// <code>
/// [ConsumeEvent]
/// public sealed class ProjectInvoice : IHandler&lt;InvoiceCreated&gt; {
///     public ValueTask&lt;Result&gt; HandleAsync(InvoiceCreated e, CancellationToken ct) {
///         // ...
///         return Result.Success();
///     }
/// }
/// </code>
/// </example>
public interface IHandler<in T> : IHandler<T, Result<Unit>> {
    /// <summary>Handles the request and returns success or an error, with no response value.</summary>
    new ValueTask<Result> HandleAsync(T request, CancellationToken ct);

    /// <summary>
    /// Adapts the no-content <see cref="HandleAsync(T, CancellationToken)"/> onto the generic
    /// <see cref="IHandler{TRequest, TResponse}"/> contract so the decorator pipeline and
    /// dispatch glue operate uniformly over <c>Result&lt;Unit&gt;</c>.
    /// </summary>
    async ValueTask<Result<Unit>> IHandler<T, Result<Unit>>.HandleAsync(T request, CancellationToken ct) {
        var result = await HandleAsync(request, ct).ConfigureAwait(false);
        return result.ToResultUnit();
    }
}
