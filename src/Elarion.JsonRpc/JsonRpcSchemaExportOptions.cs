using Elarion.Abstractions.Authorization;
using Elarion.Abstractions.ClientEvents;
using Elarion.Abstractions.Modules;

namespace Elarion.JsonRpc;

/// <summary>
/// Optional inputs for <see cref="JsonRpcSchemaExporter.Generate"/> beyond the dispatcher's methods — the
/// application's <b>capability vocabulary</b> (module names, the <c>[ClientFeatures]</c> each exposes, and the
/// permission/role catalog), exported as the schema's <c>capabilities</c> block so client generators can emit
/// typed constants instead of stringly-typed lookups. See <c>ADR-0032</c>.
/// </summary>
/// <remarks>
/// Both inputs are optional and resolved from the host's DI container by the schema-generation tool: the manifest
/// exists when a module opts in with <c>[ClientFeatures]</c> and the host calls <c>AddElarionSession</c>; the
/// catalog when the host calls <c>AddElarionAuthorization</c>. When neither is present the exported schema is
/// byte-identical to one generated without options. Only <b>enabled</b> modules contribute (the same gating the
/// exported methods already follow), and the catalog aggregates only enabled modules by construction.
/// </remarks>
public sealed record JsonRpcSchemaExportOptions {
    /// <summary>The module → exposed client-feature names manifest (<c>GetClientCapabilityManifest()</c>).</summary>
    public ClientCapabilityManifest? ClientCapabilities { get; init; }

    /// <summary>The aggregated permission/role catalog declared by handler attributes.</summary>
    public IPermissionCatalog? PermissionCatalog { get; init; }

    /// <summary>
    /// The declared client-event topics (<c>AddElarionClientEvents</c>), exported as the schema's
    /// <c>events</c> block so the TypeScript generator can emit a typed subscription client (ADR-0042).
    /// Absent or empty, the block is omitted and the schema stays byte-identical.
    /// </summary>
    public ClientEventTopicManifest? ClientEventTopics { get; init; }
}
