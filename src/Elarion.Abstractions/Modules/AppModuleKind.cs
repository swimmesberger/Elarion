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
    /// Use this for an always-on <em>domain foundation</em> — a capability with behavior that other modules
    /// assume is present (for example audit recording or current-user context enrichment). A core module is
    /// application code, not infrastructure: a domain-blind mechanism such as sending email or storing blobs
    /// belongs behind an intent-only port wired by the host, never in a core module. Keep core modules
    /// minimal — cross-cutting <em>data</em> belongs in the shared kernel and a cross-cutting
    /// <em>mechanism</em> in a port.
    /// </remarks>
    Core = 1
}
