namespace Elarion.Session;

/// <summary>
/// The deployment-resolved input the session bootstrap evaluates per user: every module with its enabled state and
/// the feature/variant names it exposes to the client (its <c>[ClientFeatures]</c> list). Built once at startup by
/// the generated <c>ElarionBootstrapper.GetClientCapabilityManifest(IConfiguration)</c> and registered as a singleton
/// by <see cref="ElarionSessionServiceCollectionExtensions.AddElarionSession"/>. See <c>ADR-0020</c>.
/// </summary>
public sealed record ClientCapabilityManifest {
    /// <summary>Every discovered module, with its enabled state and the names it exposes to the client.</summary>
    public required IReadOnlyList<ClientModuleManifest> Modules { get; init; }

    /// <summary>An empty manifest — no modules, nothing exposed.</summary>
    public static readonly ClientCapabilityManifest Empty = new() { Modules = [] };
}

/// <summary>One module's contribution to the <see cref="ClientCapabilityManifest"/>.</summary>
public sealed record ClientModuleManifest {
    /// <summary>The module name (the <c>[AppModule]</c> name, also the <c>Modules:{Name}:Enabled</c> key).</summary>
    public required string Name { get; init; }

    /// <summary>Whether the module is enabled for this deployment (deployment-scoped, not per-user).</summary>
    public required bool Enabled { get; init; }

    /// <summary>The flag/variant names this module exposes to the client (its <c>[ClientFeatures]</c> list).</summary>
    public required IReadOnlyList<string> Features { get; init; }
}
