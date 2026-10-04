namespace Elarion.Abstractions.Features;

/// <summary>
/// The runtime catalog of declared feature flags — exactly the flags of the <b>enabled</b> modules, because a
/// module's flags are registered with the module (a disabled module's flags do not exist in the deployment).
/// Gates, the session snapshot, and the JSON-RPC schema export all read the same catalog, so they cannot disagree
/// about which flags exist or which are client-exposed.
/// </summary>
public interface IFeatureFlagCatalog {
    /// <summary>Every declared flag of an enabled module, ordinally sorted by name.</summary>
    IReadOnlyList<FeatureFlagDescriptor> All { get; }

    /// <summary>Looks a flag up by its exact name.</summary>
    bool TryGet(string name, out FeatureFlagDescriptor descriptor);
}
