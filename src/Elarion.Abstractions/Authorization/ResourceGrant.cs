namespace Elarion.Abstractions.Authorization;

/// <summary>
/// A grant that shares one resource with one principal for one operation — the contract record exchanged with
/// an <see cref="IResourceGrantStore"/>. A resource is identified by an application-chosen
/// <paramref name="ResourceType"/> discriminator (matching a <c>[ResourceFilter].ResourceTypeName</c>) and a
/// stringified <paramref name="ResourceId"/>; access is granted to <paramref name="Principal"/> (a user or a
/// role) for <paramref name="Operation"/>.
/// </summary>
/// <param name="ResourceType">The resource type discriminator, e.g. <c>"Contact"</c>.</param>
/// <param name="ResourceId">The resource identifier as a string.</param>
/// <param name="Principal">The principal the resource is shared with.</param>
/// <param name="Operation">The operation the grant authorizes.</param>
public sealed record ResourceGrant(
    string ResourceType,
    string ResourceId,
    ResourcePrincipal Principal,
    ResourceOperation Operation);
