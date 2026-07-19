namespace Elarion.Abstractions;

/// <summary>
/// Marks a request as a <em>command</em> — a state-changing operation. Maps to HTTP <c>POST</c> and
/// can be targeted by decorator constraints (<c>where TRequest : ICommand</c>) or runtime checks
/// (<c>request is ICommand</c>).
/// </summary>
public interface ICommand : IRequest;

/// <summary>
/// Self-typed <see cref="ICommand"/> that additionally declares the command's success response type,
/// enabling fully inferred dispatch (see <see cref="IRequest{TSelf, TResponse}"/>):
/// <c>record Command(…) : ICommand&lt;Command, OrderId&gt;</c>.
/// </summary>
/// <typeparam name="TSelf">The implementing request type itself.</typeparam>
/// <typeparam name="TResponse">The handler's success value type (the <c>T</c> of <c>Result&lt;T&gt;</c>).</typeparam>
public interface ICommand<TSelf, TResponse> : IRequest<TSelf, TResponse>, ICommand
    where TSelf : ICommand<TSelf, TResponse>;
