namespace Elarion.Abstractions;

/// <summary>
/// Marks a class for source-generated DI registration.
/// </summary>
/// <remarks>
/// If no explicit service types are supplied, directly implemented interfaces are used.
/// If there are no direct interfaces, the implementation type is registered as itself.
/// The generated registration uses <see cref="Scope"/> to choose the DI lifetime.
/// </remarks>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class ServiceAttribute(params Type[] serviceTypes) : Attribute {
    /// <summary>
    /// Explicit service types to register.
    /// </summary>
    /// <remarks>
    /// When empty, the generator infers contracts from directly implemented interfaces. Base
    /// class interfaces are not treated as explicit contracts by this attribute.
    /// </remarks>
    public IReadOnlyList<Type> ServiceTypes { get; } = serviceTypes;

    /// <summary>
    /// Gets or sets the DI registration scope.
    /// </summary>
    public ServiceScope Scope { get; init; } = ServiceScope.Scoped;
}
