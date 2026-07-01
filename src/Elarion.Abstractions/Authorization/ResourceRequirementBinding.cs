namespace Elarion.Abstractions.Authorization;

/// <summary>
/// Binds a <see cref="RequireResourceAttribute"/> on a handler to a zero-reflection, typed accessor that reads
/// the resource id from the request. Emitted by the source generator (one per <c>[RequireResource]</c>) and
/// supplied to the authorization decorator the same way <c>HandlerMetadata</c> is — the request-shape reference
/// is bound as compile-checked C# (<c>r =&gt; r.Id</c>), never resolved by reflection at run time.
/// </summary>
/// <typeparam name="TRequest">The handler request type.</typeparam>
public sealed class ResourceRequirementBinding<TRequest>(
    Type resourceType,
    ResourceOperation operation,
    Func<TRequest, object?> idSelector) {
    /// <summary>The resource type being accessed.</summary>
    public Type ResourceType { get; } = resourceType;

    /// <summary>The operation requested.</summary>
    public ResourceOperation Operation { get; } = operation;

    /// <summary>Reads the resource id from a request instance (a generated typed member access).</summary>
    public Func<TRequest, object?> IdSelector { get; } = idSelector;
}
