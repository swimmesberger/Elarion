namespace Elarion.Abstractions.Authorization;

/// <summary>
/// Produces the stable, unambiguous discriminator string that identifies a resource type in the grants table.
/// This is the single source of truth every resource-authorization path agrees on — the grants <b>written</b>
/// through <c>IResourceGrantStore</c>, the point check in the grants-backed <c>IResourceAuthorizer</c>, and the
/// <c>EXISTS</c> subquery the <c>[ResourceFilter]</c> generator emits for its <c>Shared</c> rule must all compare
/// the same string, or a grant on one entity would silently authorize an unrelated same-named entity.
/// </summary>
/// <remarks>
/// The default discriminator is the type's <see cref="System.Type.FullName"/> (namespace-qualified), so two
/// entities named <c>Contact</c> in different modules never collide. Callers may override it with an explicit
/// string (<c>[RequireResource].ResourceTypeName</c> / <c>[ResourceFilter].ResourceTypeName</c>) when they need
/// a stable wire-independent discriminator; when they do, the same override must be used on every path.
/// Discriminators are compared with <see cref="System.StringComparison.Ordinal"/>.
/// </remarks>
public static class ResourceTypeDiscriminator {
    /// <summary>
    /// Returns the default discriminator for <paramref name="resourceType"/>: its
    /// <see cref="System.Type.FullName"/>, falling back to <see cref="System.Reflection.MemberInfo.Name"/> for the
    /// rare open-generic/array type that has no full name.
    /// </summary>
    /// <param name="resourceType">The resource CLR type.</param>
    /// <returns>The discriminator string stored and matched in the grants table.</returns>
    public static string For(Type resourceType) {
        ArgumentNullException.ThrowIfNull(resourceType);
        return resourceType.FullName ?? resourceType.Name;
    }

    /// <summary>
    /// Resolves the effective discriminator: <paramref name="explicitName"/> when it is a non-empty override,
    /// otherwise the default derived from <paramref name="resourceType"/>.
    /// </summary>
    /// <param name="resourceType">The resource CLR type.</param>
    /// <param name="explicitName">An explicit override, or <see langword="null"/>/empty to use the default.</param>
    /// <returns>The discriminator string stored and matched in the grants table.</returns>
    public static string Resolve(Type resourceType, string? explicitName) =>
        string.IsNullOrEmpty(explicitName) ? For(resourceType) : explicitName;
}
