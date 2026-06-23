namespace Elarion.Abstractions;

/// <summary>
/// Optional base marker for a handler's request type (the <c>TRequest</c> of
/// <see cref="IHandler{TRequest, TResponse}"/>).
/// </summary>
/// <remarks>
/// Implementing <see cref="ICommand"/> or <see cref="IQuery"/> declares the request's CQRS kind,
/// which the framework reads structurally at compile time — for HTTP verb inference, for decorator
/// generic constraints (<c>where TRequest : ICommand</c>), and for runtime branching
/// (<c>request is IQuery</c>). The markers are optional; nesting and naming carry no semantic weight.
/// </remarks>
public interface IRequest;
