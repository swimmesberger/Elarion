using System.Reflection;
using Elarion.Abstractions.Identity;

namespace Elarion.Abstractions.Authorization;

/// <summary>
/// A named, transport-neutral authorization policy referenced by <see cref="RequirePolicyAttribute"/>.
/// Evaluated against the current principal and the handler request — the in-process analog of an ASP.NET
/// Core policy, without the HTTP coupling. The policy <b>name</b> lives on
/// <see cref="AuthorizationPolicyAttribute"/> (for an auto-registered policy) or on the
/// <c>AddElarionAuthorizationPolicy</c> registration call — never on the implementation itself, so it is the
/// single source of truth.
/// </summary>
/// <example>
/// <code>
/// [AuthorizationPolicy("AtLeast21")]
/// public sealed class AtLeast21Policy(TimeProvider clock) : IAuthorizationPolicy {
///     public ValueTask&lt;bool&gt; EvaluateAsync(AuthorizationContext context, CancellationToken ct) {
///         var birthDate = context.User.GetClaimValues("birthdate").FirstOrDefault();
///         // ... compute age against clock.GetUtcNow() ...
///         return ValueTask.FromResult(/* age >= 21 */);
///     }
/// }
/// </code>
/// </example>
public interface IAuthorizationPolicy {
    /// <summary>Returns whether the policy is satisfied for the given context.</summary>
    ValueTask<bool> EvaluateAsync(AuthorizationContext context, CancellationToken ct);
}

/// <summary>The context supplied to an <see cref="IAuthorizationPolicy"/> and an <see cref="IGlobalAuthorizationRule"/>.</summary>
/// <param name="user">The current principal.</param>
/// <param name="resource">The handler request being authorized, if any.</param>
/// <param name="handlerType">The concrete handler being authorized, if any.</param>
public sealed class AuthorizationContext(ICurrentUser user, object? resource, Type? handlerType) {
    /// <summary>Creates a context that names no handler.</summary>
    /// <param name="user">The current principal.</param>
    /// <param name="resource">The handler request being authorized, if any.</param>
    public AuthorizationContext(ICurrentUser user, object? resource) : this(user, resource, null) { }

    /// <summary>The current principal (claims and roles).</summary>
    public ICurrentUser User { get; } = user;

    /// <summary>The handler request being authorized (the policy resource), if any.</summary>
    public object? Resource { get; } = resource;

    /// <summary>
    /// The concrete handler being authorized — independent of decorator order, like
    /// <see cref="Pipeline.HandlerMetadata.HandlerType"/> — or <see langword="null"/> on the client-event
    /// subscription path, which authorizes a topic rather than a handler. Lets a rule read the handler's own
    /// markers (see <see cref="GetHandlerAttribute{TAttribute}"/>) instead of keeping a second registry of them.
    /// </summary>
    public Type? HandlerType { get; } = handlerType;

    /// <summary>
    /// Returns the single <typeparamref name="TAttribute"/> declared on <see cref="HandlerType"/> (inherited
    /// attributes included), or <see langword="null"/> when it is absent or no handler is being authorized.
    /// </summary>
    /// <remarks>
    /// For single-use markers. An attribute with <c>AllowMultiple = true</c> declared more than once throws
    /// <see cref="System.Reflection.AmbiguousMatchException"/>; read those from <see cref="HandlerType"/> with
    /// <c>GetCustomAttributes</c>.
    /// </remarks>
    /// <example>
    /// <code>
    /// // A write must declare whether it is allowed while an operator views the app as another user.
    /// public sealed class ViewAsRule(IViewAsSession session) : IGlobalAuthorizationRule {
    ///     public ValueTask&lt;AppError?&gt; EvaluateAsync(AuthorizationContext context, CancellationToken ct) {
    ///         if (!session.IsActive || context.Resource is not ICommand) return ValueTask.FromResult&lt;AppError?&gt;(null);
    ///         return ValueTask.FromResult(context.GetHandlerAttribute&lt;AllowedDuringViewAsAttribute&gt;() is null
    ///             ? AppError.Forbidden("Not available while viewing as another user.")
    ///             : null);
    ///     }
    /// }
    /// </code>
    /// </example>
    public TAttribute? GetHandlerAttribute<TAttribute>() where TAttribute : Attribute {
        return HandlerType?.GetCustomAttribute<TAttribute>(true);
    }
}
