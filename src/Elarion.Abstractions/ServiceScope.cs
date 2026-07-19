namespace Elarion.Abstractions;

/// <summary>
/// Declares the DI lifetime used for a <see cref="ServiceAttribute"/> registration.
/// </summary>
public enum ServiceScope {
    /// <summary>
    /// Creates one instance per request or manually created service scope.
    /// </summary>
    Scoped,

    /// <summary>
    /// Creates one instance for the full application lifetime.
    /// </summary>
    /// <remarks>
    /// Use only when the service and all dependencies are thread-safe and do not depend on
    /// scoped state such as a DbContext.
    /// </remarks>
    Singleton,

    /// <summary>
    /// Creates a new instance for each resolution.
    /// </summary>
    Transient
}
