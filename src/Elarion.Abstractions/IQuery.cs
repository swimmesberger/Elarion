namespace Elarion.Abstractions;

/// <summary>
/// Marks a request as a <em>query</em> — a read-only operation. Maps to HTTP <c>GET</c> and can be
/// targeted by decorator constraints (<c>where TRequest : IQuery</c>) or runtime checks
/// (<c>request is IQuery</c>).
/// </summary>
public interface IQuery : IRequest;
