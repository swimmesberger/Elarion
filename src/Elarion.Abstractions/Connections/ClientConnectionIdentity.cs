using System.Collections.ObjectModel;
using System.Security.Claims;

namespace Elarion.Abstractions.Connections;

/// <summary>
/// A complete replacement identity for promoting an anonymous live connection: the authenticated principal,
/// its stable addressable id, and adapter-owned identity annotations.
/// </summary>
/// <remarks>
/// The registry clones the principal and defensively copies the metadata before committing a promotion.
/// Callers must replace the complete identity rather than mutate claims or metadata already exposed by a
/// <see cref="ClientConnection"/>.
/// </remarks>
public sealed record ClientConnectionIdentity {
    /// <summary>The authenticated principal that subsequent dispatches observe.</summary>
    public required ClaimsPrincipal Principal { get; init; }

    /// <summary>
    /// The non-empty stable identifier used by
    /// <see cref="IClientConnectionRegistry.GetForPrincipal(string)"/>.
    /// </summary>
    public required string PrincipalId { get; init; }

    /// <summary>Opaque bounded annotations that belong to this identity.</summary>
    public IReadOnlyDictionary<string, string> Metadata { get; init; } =
        ReadOnlyDictionary<string, string>.Empty;
}
