using Elarion.Abstractions.Identity;

namespace Elarion.Abstractions.Authorization;

/// <summary>
/// Decides whether the current principal may perform an operation on a specific resource instance — the
/// backend for <see cref="RequireResourceAttribute"/>, and the <b>escape hatch</b> a handler injects to run its
/// own per-resource validation before a write. Transport-neutral: it reads the principal from
/// <see cref="ResourceAuthorizationContext.User"/>, never from an HTTP context.
/// </summary>
/// <remarks>
/// The shipped default (in <c>Elarion.Authorization.EntityFrameworkCore</c>) authorizes via the resource-grants
/// table (a share with the caller's user or any of their roles). Owner-based access is the handler's concern
/// via the escape hatch (load the entity and call <c>IQueryAuthorizer&lt;T&gt;.Matches</c>, or model ownership
/// as a grant). When no implementation is registered, the framework fails closed (denies).
/// </remarks>
public interface IResourceAuthorizer {
    /// <summary>Returns whether the principal in <paramref name="context"/> is authorized for that resource.</summary>
    ValueTask<bool> AuthorizeResourceAsync(ResourceAuthorizationContext context, CancellationToken ct);
}

/// <summary>The context supplied to an <see cref="IResourceAuthorizer"/>.</summary>
public sealed class ResourceAuthorizationContext(
    ICurrentUser user,
    Type resourceType,
    ResourceOperation operation,
    object? resourceId) {
    /// <summary>The current principal (claims and roles).</summary>
    public ICurrentUser User { get; } = user;

    /// <summary>The resource type being accessed.</summary>
    public Type ResourceType { get; } = resourceType;

    /// <summary>The operation requested.</summary>
    public ResourceOperation Operation { get; } = operation;

    /// <summary>The resource id, or <see langword="null"/> if the request did not supply one.</summary>
    public object? ResourceId { get; } = resourceId;
}
