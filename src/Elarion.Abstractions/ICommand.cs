namespace Elarion.Abstractions;

/// <summary>
/// Marks a request as a <em>command</em> — a state-changing operation. Maps to HTTP <c>POST</c> and
/// can be targeted by decorator constraints (<c>where TRequest : ICommand</c>) or runtime checks
/// (<c>request is ICommand</c>).
/// </summary>
public interface ICommand : IRequest;
