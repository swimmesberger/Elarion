namespace Elarion.Abstractions.Modules;

/// <summary>
/// The deployment-resolved module map the session bootstrap projects: every module with its enabled state. Built
/// once at startup by the generated <c>ElarionBootstrapper.GetClientCapabilityManifest(IConfiguration)</c> and
/// registered as a singleton by <c>AddElarionSession</c> (<c>Elarion.Session</c>). The flags a client may see
/// are not part of it — they are the <see cref="Features.FeatureFlagDescriptor.ExposeToClient"/> entries of the
/// <see cref="Features.IFeatureFlagCatalog"/>, so the module map and the flag vocabulary each have one owner.
/// Lives in Abstractions so transport packages (e.g. the JSON-RPC schema exporter, which references only
/// Abstractions) can consume it too. See <c>ADR-0030</c> and <c>ADR-0079</c>.
/// </summary>
public sealed record ClientCapabilityManifest {
    /// <summary>Every discovered module with its enabled state.</summary>
    public required IReadOnlyList<ClientModuleManifest> Modules { get; init; }

    /// <summary>An empty manifest — no modules.</summary>
    public static readonly ClientCapabilityManifest Empty = new() { Modules = [] };
}

/// <summary>One module's entry in the <see cref="ClientCapabilityManifest"/>.</summary>
public sealed record ClientModuleManifest {
    /// <summary>The module name (the <c>[AppModule]</c> name, also the <c>Modules:{Name}:Enabled</c> key).</summary>
    public required string Name { get; init; }

    /// <summary>Whether the module is enabled for this deployment (deployment-scoped, not per-user).</summary>
    public required bool Enabled { get; init; }
}
