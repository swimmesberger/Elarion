using Elarion.Abstractions.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace Elarion.Abstractions.Authorization;

/// <summary>
/// Decides whether the current principal may perform an operation on a specific resource instance — the
/// backend for <see cref="RequireResourceAttribute"/>, and the <b>escape hatch</b> a handler injects to run its
/// own per-resource validation before a write. Transport-neutral: it reads the principal from
/// <see cref="ResourceAuthorizationContext.User"/>, never from an HTTP context.
/// </summary>
/// <remarks>
/// <para>
/// <b>One check per resource type, then a general backend.</b> A check registered for a resource type answers for
/// that type: the filter-backed check (<c>AddElarionResourceFilterCheck</c> in
/// <c>Elarion.Authorization.EntityFrameworkCore</c>) asks the entity's <c>IQueryAuthorizer&lt;T&gt;</c>, so the point
/// check and the list filter are one rule — owner, sharing, bypass and every composed alternative included. Every
/// other type goes to the general backend: the resource-grants table (a share with the caller's user or any of their
/// roles) once <c>AddElarionResourceAuthorization</c> is called, and otherwise a fail-closed denial.
/// </para>
/// <para>
/// A per-type check is a keyed <see cref="IResourceAuthorizer"/> whose service key is the resource
/// <see cref="Type"/>; the shipped backends route to it through <see cref="ResourceTypeAuthorizer.Find"/>, and a
/// host-written backend that wants the same routing calls it too.
/// </para>
/// </remarks>
public interface IResourceAuthorizer {
    /// <summary>Returns whether the principal in <paramref name="context"/> is authorized for that resource.</summary>
    ValueTask<bool> AuthorizeResourceAsync(ResourceAuthorizationContext context, CancellationToken ct);
}

/// <summary>
/// Finds the point check registered for one resource type — a keyed <see cref="IResourceAuthorizer"/> whose service
/// key is the resource <see cref="Type"/>.
/// </summary>
/// <example>
/// <code>
/// // A hand-written check for one resource type.
/// services.AddKeyedScoped&lt;IResourceAuthorizer, InvoiceAuthorizer&gt;(typeof(Invoice));
/// </code>
/// </example>
public static class ResourceTypeAuthorizer {
    /// <summary>
    /// The check registered for <paramref name="resourceType"/>, or <see langword="null"/> when the type has none (the
    /// general backend then decides). The type is matched exactly; a base type's check does not cover a derived type.
    /// </summary>
    /// <param name="services">The scope the check is resolved from.</param>
    /// <param name="resourceType">The resource type of the point check.</param>
    public static IResourceAuthorizer? Find(IServiceProvider services, Type resourceType) {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(resourceType);
        // A container without keyed services has no per-type checks; asking it through the extension would throw.
        return services is IKeyedServiceProvider keyed
            ? keyed.GetKeyedService(typeof(IResourceAuthorizer), resourceType) as IResourceAuthorizer
            : null;
    }
}

/// <summary>The context supplied to an <see cref="IResourceAuthorizer"/>.</summary>
public sealed class ResourceAuthorizationContext(
    ICurrentUser user,
    Type resourceType,
    string resourceTypeName,
    ResourceOperation operation,
    object? resourceId) {
    /// <summary>The current principal (claims and roles).</summary>
    public ICurrentUser User { get; } = user;

    /// <summary>The resource type being accessed.</summary>
    public Type ResourceType { get; } = resourceType;

    /// <summary>
    /// The resource-type discriminator matched against the grants table — the stable, unambiguous string
    /// (default <see cref="System.Type.FullName"/>) every resource-authorization path agrees on. Compared with
    /// <see cref="System.StringComparison.Ordinal"/>. Prefer this over <see cref="ResourceType"/>'s name when
    /// looking up grants.
    /// </summary>
    public string ResourceTypeName { get; } = resourceTypeName;

    /// <summary>The operation requested.</summary>
    public ResourceOperation Operation { get; } = operation;

    /// <summary>The resource id, or <see langword="null"/> if the request did not supply one.</summary>
    public object? ResourceId { get; } = resourceId;
}
