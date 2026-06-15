namespace Elarion.Abstractions.Modules;

/// <summary>
/// Describes how an application module participates in host bootstrapping.
/// </summary>
public enum AppModuleKind {
    /// <summary>
    /// Optional feature module that can be disabled through configuration.
    /// </summary>
    /// <remarks>
    /// Feature modules are intended for application capabilities that can be toggled without
    /// removing the assembly from the host.
    /// </remarks>
    Feature = 0,

    /// <summary>
    /// Required foundation module that is always enabled and initialized before features.
    /// </summary>
    /// <remarks>
    /// Use this for shared infrastructure or domain foundations that feature modules depend on.
    /// </remarks>
    Core = 1,
}
