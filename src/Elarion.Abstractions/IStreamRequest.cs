namespace Elarion.Abstractions;

/// <summary>
/// Optional self-typed marker for a stream handler's request (the <c>TRequest</c> of
/// <see cref="IStreamHandler{TRequest, TItem}"/>) that additionally declares the stream's item type,
/// enabling fully inferred stream dispatch: <c>record Tail(…) : IStreamRequest&lt;Tail, LogLine&gt;</c>
/// lets <c>StreamHandlerInvoker.InvokeAsync(request, …)</c> infer both generic arguments.
/// </summary>
/// <remarks>
/// The stream shape is deliberately not an <see cref="IRequest{TSelf, TResponse}"/>: a stream's
/// "response" is a deferred item sequence, not a <c>Result&lt;TResponse&gt;</c> value, so the markers
/// stay distinct just like <see cref="IStreamHandler{TRequest, TItem}"/> and
/// <see cref="IHandler{TRequest, TResponse}"/> do. Combine with <see cref="IQuery"/>/<see cref="ICommand"/>
/// for the CQRS kind if needed. Like every request marker, this one is optional.
/// </remarks>
/// <typeparam name="TSelf">The implementing request type itself.</typeparam>
/// <typeparam name="TItem">The item type the accepted stream yields.</typeparam>
public interface IStreamRequest<TSelf, TItem> : IRequest where TSelf : IStreamRequest<TSelf, TItem>;
