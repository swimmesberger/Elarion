namespace Elarion.Abstractions.Authorization;

/// <summary>
/// A resolved per-call resource requirement: the principal must be authorized to perform
/// <paramref name="Operation"/> on the resource of type <paramref name="ResourceType"/> identified by
/// <paramref name="ResourceId"/> (read from the request). Evaluated by an <see cref="IResourceAuthorizer"/>.
/// </summary>
/// <param name="ResourceType">The resource type being accessed.</param>
/// <param name="Operation">The operation requested.</param>
/// <param name="ResourceId">The resource id resolved from the request, or <see langword="null"/> if absent.</param>
public sealed record ResourceRequirement(Type ResourceType, ResourceOperation Operation, object? ResourceId);
