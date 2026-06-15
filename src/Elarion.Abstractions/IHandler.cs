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
