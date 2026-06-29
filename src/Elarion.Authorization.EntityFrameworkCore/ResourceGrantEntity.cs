namespace Elarion.Authorization.EntityFrameworkCore;

/// <summary>
/// The persisted row backing the resource-grants table: one share of a resource with a principal for an
/// operation. The <c>Elarion.Abstractions.Authorization.ResourcePrincipal</c> is flattened into the
/// <see cref="PrincipalKind"/> + <see cref="PrincipalId"/> columns. The composite key is
/// <c>(ResourceType, ResourceId, PrincipalKind, PrincipalId, Operation)</c>, so a given share is recorded at
/// most once.
/// </summary>
public sealed class ResourceGrantEntity {
    /// <summary>The resource type discriminator, e.g. <c>"Contact"</c>.</summary>
    public required string ResourceType { get; init; }

    /// <summary>The resource identifier as a string.</summary>
    public required string ResourceId { get; init; }

    /// <summary>The principal kind, e.g. <c>"user"</c> or <c>"role"</c>.</summary>
    public required string PrincipalKind { get; init; }

    /// <summary>The principal identifier: a user id or a role name.</summary>
    public required string PrincipalId { get; init; }

    /// <summary>The operation this grant authorizes, e.g. <c>"read"</c>.</summary>
    public required string Operation { get; init; }
}
