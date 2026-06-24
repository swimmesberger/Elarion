using System.Reflection;

namespace Elarion.Abstractions.Pipeline;

/// <summary>
/// Compile-time metadata about the concrete handler at the bottom of a decorator pipeline, supplied
/// by the source generator to any decorator that declares a <see cref="HandlerMetadata"/> constructor
/// parameter.
/// </summary>
/// <remarks>
/// <para>
/// Decorators wrap innermost-first, so a decorator that tries to read the handler's attributes via
/// <c>inner.GetType()</c> only sees the concrete handler when it happens to be the innermost wrapper —
/// at any other position it sees the next decorator instead. For an authorization-style decorator that
/// reads a <c>[RequirePermission]</c>-style attribute off the handler, that is a <b>fail-open</b>
/// footgun: positioned outermost (the intuitive "check first" spot) the attribute is invisible and the
/// check silently passes.
/// </para>
/// <para>
/// <see cref="HandlerType"/> is the <b>true</b> concrete handler type regardless of the decorator's
/// position in the chain, so attribute-driven cross-cutting concerns become position-independent.
/// Declare a constructor parameter of this type and the generator injects it (the inner handler comes
/// first, then any DI dependencies and this metadata in declaration order):
/// </para>
/// <example>
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
/// </example>
/// <para>
/// Reading attributes still uses reflection, but on a single known <see cref="System.Type"/>, which is
/// AOT/trim-safe as long as the attribute type itself is preserved.
/// </para>
/// </remarks>
public sealed class HandlerMetadata {
    /// <summary>Creates metadata for the given concrete handler type.</summary>
    /// <param name="handlerType">The concrete handler type at the bottom of the pipeline.</param>
    public HandlerMetadata(Type handlerType) {
        HandlerType = handlerType ?? throw new ArgumentNullException(nameof(handlerType));
    }

    /// <summary>The concrete handler type, independent of the decorator's position in the chain.</summary>
    public Type HandlerType { get; }

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
