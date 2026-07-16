using Elarion.Abstractions.Results;

namespace Elarion.Abstractions;

/// <summary>
/// Handles a request whose successful response is a deferred sequence of items.
/// </summary>
/// <remarks>
/// Startup is deliberately separate from enumeration. A failed <see cref="Result{T}"/> rejects the request
/// before a transport commits its response; once accepted, completion, cancellation, and exceptions belong to
/// the returned sequence. This is not an <see cref="IHandler{TRequest,TResponse}"/> shape.
/// </remarks>
/// <typeparam name="TRequest">The request type.</typeparam>
/// <typeparam name="TItem">The item type yielded after startup succeeds.</typeparam>
public interface IStreamHandler<in TRequest, TItem> {
    /// <summary>Accepts or rejects the request and, on success, returns its lazy response sequence.</summary>
    ValueTask<Result<IAsyncEnumerable<TItem>>> HandleAsync(TRequest request, CancellationToken ct);
}
