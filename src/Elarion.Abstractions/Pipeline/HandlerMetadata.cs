using System.Reflection;

namespace Elarion.Abstractions.Pipeline;

/// <summary>
/// Compile-time facts about the concrete handler at the bottom of a decorator pipeline — its type, request
/// type, response type, and (through them) its attributes — supplied by the source generator.
/// </summary>
/// <remarks>
/// <para>
/// It is used in two places, and both let a decorator key off the <b>handler's own attributes</b> without the
/// fail-open footgun of reading <c>inner.GetType()</c> (which only sees the concrete handler when the decorator
/// happens to be innermost — at any other position it sees the next decorator):
/// </para>
/// <list type="number">
/// <item>
/// <description>
/// <b>Constructor injection.</b> A decorator that declares a <see cref="HandlerMetadata"/> constructor parameter
/// receives it (the inner handler comes first, then any DI dependencies and this metadata in declaration order),
/// so an authorization-style decorator can read a <c>[RequirePermission]</c>-style attribute at run time:
/// <code>
/// public sealed class AuthorizationDecorator&lt;TRequest, TResponse&gt;(
///     IHandler&lt;TRequest, TResponse&gt; inner,
///     HandlerMetadata metadata,
///     ICurrentUser user)
///     : IHandler&lt;TRequest, TResponse&gt;
/// {
///     public ValueTask&lt;TResponse&gt; HandleAsync(TRequest request, CancellationToken ct)
///     {
///         var required = metadata.GetAttribute&lt;RequirePermissionAttribute&gt;();
///         // ... enforce `required` against `user`, then call inner ...
///     }
/// }
/// </code>
/// </description>
/// </item>
/// <item>
/// <description>
/// <b>An <c>AppliesTo</c> attachment predicate.</b> A decorator may declare
/// <c>public static bool AppliesTo(HandlerMetadata handler)</c> so it attaches <b>based on the handler</b> — its
/// attributes, request type (<see cref="RequestType"/>), or response type — which is the same capability the
/// framework's built-in decorators use, available to any custom decorator:
/// <code>
/// public static bool AppliesTo(HandlerMetadata handler) =>
///     handler.GetAttribute&lt;AuditableAttribute&gt;() is not null;
/// </code>
/// The generator calls it once per closed handler type at pipeline-build time and caches the result.
/// </description>
/// </item>
/// </list>
/// <para>
/// Reading attributes uses reflection, but on a single known <see cref="System.Type"/>, which is AOT/trim-safe as
/// long as the attribute type itself is preserved.
/// </para>
/// </remarks>
public sealed class HandlerMetadata {
    /// <summary>Creates metadata for the given concrete handler.</summary>
    /// <param name="handlerType">The concrete handler type at the bottom of the pipeline.</param>
    /// <param name="requestType">The handler's request type (<c>TRequest</c>).</param>
    /// <param name="responseType">The handler's response type (<c>TResponse</c>, typically a <c>Result&lt;T&gt;</c>).</param>
    /// <param name="pipelineAccessor">
    /// Optional late-bound accessor onto the resolved pipeline for <see cref="Pipeline"/> (the generator supplies
    /// one reading its per-handler cache). When omitted, <see cref="Pipeline"/> reports an empty pipeline.
    /// </param>
    public HandlerMetadata(
        Type handlerType,
        Type requestType,
        Type responseType,
        Func<IReadOnlyList<PipelineStep>>? pipelineAccessor = null) {
        HandlerType = handlerType ?? throw new ArgumentNullException(nameof(handlerType));
        RequestType = requestType ?? throw new ArgumentNullException(nameof(requestType));
        ResponseType = responseType ?? throw new ArgumentNullException(nameof(responseType));
        Pipeline = new HandlerPipeline(pipelineAccessor);
    }

    /// <summary>The concrete handler type, independent of the decorator's position in the chain.</summary>
    public Type HandlerType { get; }

    /// <summary>The handler's request type (the <c>TRequest</c> of its <c>IHandler&lt;TRequest, TResponse&gt;</c>).</summary>
    public Type RequestType { get; }

    /// <summary>The handler's response type (the <c>TResponse</c>; typically a <c>Result&lt;T&gt;</c>).</summary>
    public Type ResponseType { get; }

    /// <summary>
    /// The decorators actually wrapping this handler in the current process, in execution order. Unlike the
    /// members above, this is <b>runtime-resolved</b>: empty until the handler is first resolved from DI, then
    /// the composed pipeline (see <see cref="IHandlerPipeline"/> for its caveats). Surfaced on the handler span
    /// as <c>elarion.handler.pipeline</c>.
    /// </summary>
    public IHandlerPipeline Pipeline { get; }

    /// <summary>
    /// Returns the single attribute of type <typeparamref name="TAttribute"/> declared on the handler,
    /// or <c>null</c> if absent.
    /// </summary>
    public TAttribute? GetAttribute<TAttribute>() where TAttribute : Attribute =>
        HandlerType.GetCustomAttribute<TAttribute>(inherit: true);

    /// <summary>Returns all attributes of type <typeparamref name="TAttribute"/> declared on the handler.</summary>
    public IEnumerable<TAttribute> GetAttributes<TAttribute>() where TAttribute : Attribute =>
        HandlerType.GetCustomAttributes<TAttribute>(inherit: true);
}
