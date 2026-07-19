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

/// <summary>
/// Optional self-typed marker that additionally declares the request's success response type — the
/// <c>TResponse</c> of its <c>IHandler&lt;TSelf, Result&lt;TResponse&gt;&gt;</c> handler.
/// </summary>
/// <remarks>
/// <para>
/// Implementing this marker (or <see cref="ICommand{TSelf, TResponse}"/> /
/// <see cref="IQuery{TSelf, TResponse}"/>) lets dispatch entry points such as
/// <c>IHandlerSender.SendAsync</c>, <c>HandlerInvoker.InvokeAsync</c>, and
/// <c>ConnectionHandlerInvoker.InvokeAsync</c> infer <b>both</b> generic arguments from the request
/// argument alone — <c>await sender.SendAsync(new GetClient.Query(id), ct)</c> instead of
/// <c>await sender.SendAsync&lt;GetClient.Query, GetClient.Response&gt;(…)</c>. Inference is purely
/// compile-time: dispatch stays statically typed through the same <c>IHandler&lt;,&gt;</c> resolution,
/// with no reflection, registry, or boxing.
/// </para>
/// <para>
/// <typeparamref name="TSelf"/> is the implementing request type itself (the curiously recurring
/// pattern, as in <see cref="IParsable{TSelf}"/>): <c>record Query(Guid Id) : IQuery&lt;Query, Response&gt;</c>.
/// The declared <typeparamref name="TResponse"/> must match the handler's <c>Result&lt;TResponse&gt;</c>
/// response — the marker analyzer enforces both invariants at build time (<c>ELREQ001</c> for a
/// <typeparamref name="TSelf"/> that names a different type, <c>ELREQ002</c> for a handler response that
/// drifts from the declared <typeparamref name="TResponse"/>). Like every request marker, this one is
/// optional: requests without it use the explicit two-generic dispatch overloads.
/// </para>
/// </remarks>
/// <typeparam name="TSelf">The implementing request type itself.</typeparam>
/// <typeparam name="TResponse">The handler's success value type (the <c>T</c> of <c>Result&lt;T&gt;</c>).</typeparam>
public interface IRequest<TSelf, TResponse> : IRequest where TSelf : IRequest<TSelf, TResponse>;
