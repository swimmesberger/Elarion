namespace Elarion.Abstractions;

/// <summary>
/// Marks a request as a <em>query</em> — a read-only operation. Maps to HTTP <c>GET</c> and can be
/// targeted by decorator constraints (<c>where TRequest : IQuery</c>) or runtime checks
/// (<c>request is IQuery</c>).
/// </summary>
public interface IQuery : IRequest;

/// <summary>
/// Self-typed <see cref="IQuery"/> that additionally declares the query's success response type,
/// enabling fully inferred dispatch (see <see cref="IRequest{TSelf, TResponse}"/>):
/// <c>record Query(Guid Id) : IQuery&lt;Query, Response&gt;</c>.
/// </summary>
/// <typeparam name="TSelf">The implementing request type itself.</typeparam>
/// <typeparam name="TResponse">The handler's success value type (the <c>T</c> of <c>Result&lt;T&gt;</c>).</typeparam>
public interface IQuery<TSelf, TResponse> : IRequest<TSelf, TResponse>, IQuery
    where TSelf : IQuery<TSelf, TResponse>;
